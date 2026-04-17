using System.Diagnostics;
using Marten;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using Rebus.Sagas;
using WorkerSagaDemo.Contracts.Contracts;
using WorkerSagaDemo.Contracts.Domain;
using WorkerSagaDemo.Worker.Ai;

namespace WorkerSagaDemo.Worker.Sagas;

public class JobProcessingSaga :
    Saga<JobProcessingSagaData>,
    IAmInitiatedBy<StartJobCommand>,
    IHandleMessages<ProcessJobStep>,
    IHandleMessages<JobTimedOut>
{
    private readonly IDocumentSession _session;
    private readonly ILogger<JobProcessingSaga> _logger;
    private readonly IBus _bus;
    private readonly IJobAiService _aiService;

    public JobProcessingSaga(
        IDocumentSession session,
        ILogger<JobProcessingSaga> logger,
        IBus bus,
        IJobAiService aiService)
    {
        _session = session;
        _logger = logger;
        _bus = bus;
        _aiService = aiService;
    }

    protected override void CorrelateMessages(ICorrelationConfig<JobProcessingSagaData> config)
    {
        config.Correlate<StartJobCommand>(m => m.JobId, d => d.JobId);
        config.Correlate<ProcessJobStep>(m => m.JobId, d => d.JobId);
        config.Correlate<JobTimedOut>(m => m.JobId, d => d.JobId);
    }

    public async Task Handle(StartJobCommand message)
    {
        using var activity = SagaTelemetry.ActivitySource.StartActivity(
            "Saga.StartJobCommand",
            ActivityKind.Consumer);
        activity?.SetTag(SagaTelemetry.TagJobId, message.JobId);

        _logger.LogInformation("Saga initiated for Job {JobId}", message.JobId);

        var job = await _session.LoadAsync<Job>(message.JobId);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} not found, marking saga complete", message.JobId);
            activity?.SetTag(SagaTelemetry.TagOutcome, "job-not-found");
            MarkAsComplete();
            return;
        }

        Data.JobId = message.JobId;
        Data.TotalSteps = job.Steps.Count;
        Data.CompletedSteps = 0;

        activity?.SetTag(SagaTelemetry.TagStepCount, Data.TotalSteps);

        // --- AI Classification (Session C) ---
        // Classify the trade description before scheduling processing steps.
        // This is a non-blocking enrichment: if the classifier is down or the
        // description is missing, the saga continues with Unknown classification.
        // The classification result is stored on saga data for later use (e.g.
        // routing, priority, UI display).
        if (!string.IsNullOrWhiteSpace(job.Description))
        {
            using var classifyActivity = SagaTelemetry.ActivitySource.StartActivity(
                "Saga.ClassifyTrade",
                ActivityKind.Internal);
            classifyActivity?.SetTag(SagaTelemetry.TagJobId, message.JobId);

            try
            {
                var classification = await _aiService.ClassifyTradeAsync(job.Description);

                Data.TradeCategory = classification.Category.ToString();
                Data.RiskTier = classification.RiskTier.ToString();
                Data.ClassificationRationale = classification.Rationale;

                // Persist classification on the Job document so GET /jobs/{id}
                // returns it and the UI can display it.
                job.TradeCategory = Data.TradeCategory;
                job.RiskTier = Data.RiskTier;
                _session.Store(job);
                await _session.SaveChangesAsync();

                classifyActivity?.SetTag(SagaTelemetry.TagTradeCategory, Data.TradeCategory);
                classifyActivity?.SetTag(SagaTelemetry.TagRiskTier, Data.RiskTier);
                classifyActivity?.SetTag(SagaTelemetry.TagOutcome, "classified");

                _logger.LogInformation(
                    "Job {JobId} classified: Category={Category}, RiskTier={RiskTier}, Rationale=\"{Rationale}\"",
                    message.JobId, Data.TradeCategory, Data.RiskTier, classification.Rationale);
            }
            catch (ClassifierUnavailableException ex)
            {
                Data.TradeCategory = "Unknown";
                Data.RiskTier = "Unknown";
                Data.ClassificationRationale = "Classifier unavailable";

                job.TradeCategory = Data.TradeCategory;
                job.RiskTier = Data.RiskTier;
                _session.Store(job);
                await _session.SaveChangesAsync();

                classifyActivity?.SetStatus(ActivityStatusCode.Error, "Classifier unavailable");
                classifyActivity?.SetTag(SagaTelemetry.TagOutcome, "classifier-unavailable");

                _logger.LogWarning(ex,
                    "Job {JobId} classification failed (classifier unavailable). Continuing with Unknown.",
                    message.JobId);
            }
            catch (ClassifierParseException ex)
            {
                Data.TradeCategory = "Unknown";
                Data.RiskTier = "Unknown";
                Data.ClassificationRationale = $"Parse error: {ex.Message}";

                job.TradeCategory = Data.TradeCategory;
                job.RiskTier = Data.RiskTier;
                _session.Store(job);
                await _session.SaveChangesAsync();

                classifyActivity?.SetStatus(ActivityStatusCode.Error, "Classification parse error");
                classifyActivity?.SetTag(SagaTelemetry.TagOutcome, "parse-error");

                _logger.LogWarning(ex,
                    "Job {JobId} classification failed (parse error). Continuing with Unknown.",
                    message.JobId);
            }
        }
        else
        {
            _logger.LogDebug("Job {JobId} has no description, skipping classification", message.JobId);
        }
        // --- End AI Classification ---

        await _bus.DeferLocal(TimeSpan.FromSeconds(30), new JobTimedOut(message.JobId));
        await _bus.DeferLocal(TimeSpan.FromSeconds(1), new ProcessJobStep(message.JobId, 0));

        activity?.SetTag(SagaTelemetry.TagOutcome, "initiated");
        _logger.LogInformation("Saga scheduled {StepCount} steps for Job {JobId}", Data.TotalSteps, message.JobId);
    }

    public async Task Handle(ProcessJobStep message)
    {
        using var activity = SagaTelemetry.ActivitySource.StartActivity(
            "Saga.ProcessJobStep",
            ActivityKind.Consumer);
        activity?.SetTag(SagaTelemetry.TagJobId, message.JobId);
        activity?.SetTag(SagaTelemetry.TagStepIndex, message.StepIndex);

        if (Data.IsComplete)
        {
            activity?.SetTag(SagaTelemetry.TagOutcome, "skipped-already-complete");
            return;
        }

        var job = await _session.LoadAsync<Job>(message.JobId);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} disappeared mid-saga", message.JobId);
            activity?.SetTag(SagaTelemetry.TagOutcome, "job-disappeared");
            MarkAsComplete();
            return;
        }

        var stepIndex = message.StepIndex;

        if (stepIndex >= job.Steps.Count)
        {
            job.Status = "Completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _session.Store(job);
            await _session.SaveChangesAsync();

            await PublishStatusChangedAsync(job);

            Data.IsComplete = true;
            MarkAsComplete();
            activity?.SetTag(SagaTelemetry.TagOutcome, "job-completed");
            _logger.LogInformation("Job {JobId} completed all steps. Saga complete.", job.Id);
            return;
        }

        var step = job.Steps[stepIndex];
        activity?.SetTag(SagaTelemetry.TagStepName, step.Name);

        step.Status = "InProgress";
        step.StartedAt = DateTimeOffset.UtcNow;
        job.Status = $"Processing:{step.Name}";
        _session.Store(job);
        await _session.SaveChangesAsync();
        _logger.LogInformation("Job {JobId} - Step '{StepName}' started", job.Id, step.Name);

        await PublishStatusChangedAsync(job);

        using (var workActivity = SagaTelemetry.ActivitySource.StartActivity(
            $"Saga.Work.{step.Name}",
            ActivityKind.Internal))
        {
            workActivity?.SetTag(SagaTelemetry.TagJobId, message.JobId);
            workActivity?.SetTag(SagaTelemetry.TagStepName, step.Name);
            await Task.Delay(1000);
        }

        step.Status = "Completed";
        step.CompletedAt = DateTimeOffset.UtcNow;
        Data.CompletedSteps++;
        _session.Store(job);
        await _session.SaveChangesAsync();

        activity?.SetTag(SagaTelemetry.TagCompletedSteps, Data.CompletedSteps);
        activity?.SetTag(SagaTelemetry.TagOutcome, "step-completed");

        _logger.LogInformation("Job {JobId} - Step '{StepName}' completed ({Done}/{Total})",
            job.Id, step.Name, Data.CompletedSteps, Data.TotalSteps);

        await _bus.DeferLocal(TimeSpan.FromSeconds(3), new ProcessJobStep(message.JobId, stepIndex + 1));
    }

    public async Task Handle(JobTimedOut message)
    {
        using var activity = SagaTelemetry.ActivitySource.StartActivity(
            "Saga.JobTimedOut",
            ActivityKind.Consumer);
        activity?.SetTag(SagaTelemetry.TagJobId, message.JobId);
        activity?.SetTag(SagaTelemetry.TagCompletedSteps, Data.CompletedSteps);
        activity?.SetTag(SagaTelemetry.TagStepCount, Data.TotalSteps);

        if (Data.IsComplete)
        {
            activity?.SetTag(SagaTelemetry.TagOutcome, "skipped-already-complete");
            return;
        }

        _logger.LogWarning("Job {JobId} timed out. Completed {Done}/{Total} steps.",
            message.JobId, Data.CompletedSteps, Data.TotalSteps);

        var job = await _session.LoadAsync<Job>(message.JobId);
        if (job is not null)
        {
            job.Status = "Failed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _session.Store(job);
            await _session.SaveChangesAsync();

            await PublishStatusChangedAsync(job);
        }

        Data.IsComplete = true;
        MarkAsComplete();

        activity?.SetStatus(ActivityStatusCode.Error, "Saga timed out");
        activity?.SetTag(SagaTelemetry.TagOutcome, "timed-out");
    }

    private Task PublishStatusChangedAsync(Job job) =>
        _bus.Publish(new JobStatusChanged(
            job.Id,
            job.Status,
            Data.CompletedSteps,
            Data.TotalSteps,
            DateTimeOffset.UtcNow,
            job.Description,
            Data.TradeCategory,
            Data.RiskTier));
}
