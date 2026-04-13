using System.Diagnostics;
using Marten;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using Rebus.Sagas;
using WorkerSagaDemo.Contracts.Contracts;
using WorkerSagaDemo.Contracts.Domain;

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

    public JobProcessingSaga(IDocumentSession session, ILogger<JobProcessingSaga> logger, IBus bus)
    {
        _session = session;
        _logger = logger;
        _bus = bus;
    }

    protected override void CorrelateMessages(ICorrelationConfig<JobProcessingSagaData> config)
    {
        config.Correlate<StartJobCommand>(m => m.JobId, d => d.JobId);
        config.Correlate<ProcessJobStep>(m => m.JobId, d => d.JobId);
        config.Correlate<JobTimedOut>(m => m.JobId, d => d.JobId);
    }

    public async Task Handle(StartJobCommand message)
    {
        // Open an OpenTelemetry span that covers the entire handler.
        // ActivityKind.Consumer means "this is work triggered by an incoming
        // message" -- distinct from Server (HTTP request) or Internal.
        // StartActivity returns null if nothing is listening; the null-conditional
        // operators below handle that gracefully.
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

        // Simulate the real work this step would do (e.g. calling an external
        // service, running a computation, talking to a database). Wrapped in
        // its own nested span so the timing of the "work" is isolated from
        // the surrounding Marten I/O.
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

        // SetStatus on Activity uses OTel's Error status, which the Aspire
        // dashboard renders with a red badge. This makes failed sagas
        // immediately visible in the Traces tab.
        activity?.SetStatus(ActivityStatusCode.Error, "Saga timed out");
        activity?.SetTag(SagaTelemetry.TagOutcome, "timed-out");
    }

    /// <summary>
    /// Publishes a JobStatusChanged event so subscribers (the API's SignalR
    /// forwarder) can push real-time updates to connected browser clients.
    /// </summary>
    private Task PublishStatusChangedAsync(Job job) =>
        _bus.Publish(new JobStatusChanged(
            job.Id,
            job.Status,
            Data.CompletedSteps,
            Data.TotalSteps,
            DateTimeOffset.UtcNow));
}
