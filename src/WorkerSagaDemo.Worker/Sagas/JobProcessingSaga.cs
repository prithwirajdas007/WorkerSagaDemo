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
        _logger.LogInformation("Saga initiated for Job {JobId}", message.JobId);

        var job = await _session.LoadAsync<Job>(message.JobId);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} not found, marking saga complete", message.JobId);
            MarkAsComplete();
            return;
        }

        Data.JobId = message.JobId;
        Data.TotalSteps = job.Steps.Count;
        Data.CompletedSteps = 0;

        await _bus.DeferLocal(TimeSpan.FromSeconds(30), new JobTimedOut(message.JobId));
        await _bus.DeferLocal(TimeSpan.FromSeconds(1), new ProcessJobStep(message.JobId, 0));

        _logger.LogInformation("Saga scheduled {StepCount} steps for Job {JobId}", Data.TotalSteps, message.JobId);
    }

    public async Task Handle(ProcessJobStep message)
    {
        if (Data.IsComplete) return;

        var job = await _session.LoadAsync<Job>(message.JobId);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} disappeared mid-saga", message.JobId);
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
            _logger.LogInformation("Job {JobId} completed all steps. Saga complete.", job.Id);
            return;
        }

        var step = job.Steps[stepIndex];

        step.Status = "InProgress";
        step.StartedAt = DateTimeOffset.UtcNow;
        job.Status = $"Processing:{step.Name}";
        _session.Store(job);
        await _session.SaveChangesAsync();
        _logger.LogInformation("Job {JobId} - Step '{StepName}' started", job.Id, step.Name);

        await PublishStatusChangedAsync(job);

        await Task.Delay(1000);

        step.Status = "Completed";
        step.CompletedAt = DateTimeOffset.UtcNow;
        Data.CompletedSteps++;
        _session.Store(job);
        await _session.SaveChangesAsync();
        _logger.LogInformation("Job {JobId} - Step '{StepName}' completed ({Done}/{Total})",
            job.Id, step.Name, Data.CompletedSteps, Data.TotalSteps);

        await _bus.DeferLocal(TimeSpan.FromSeconds(3), new ProcessJobStep(message.JobId, stepIndex + 1));
    }

    public async Task Handle(JobTimedOut message)
    {
        if (Data.IsComplete) return;

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
