namespace WorkerSagaDemo.Contracts.Contracts;

/// <summary>
/// Published by the saga whenever a Job's status changes.
/// Subscribers (e.g. the API's SignalR forwarder) receive this and push
/// notifications to connected clients.
/// </summary>
public record JobStatusChanged(
    Guid JobId,
    string Status,
    int CompletedSteps,
    int TotalSteps,
    DateTimeOffset ChangedAt);
