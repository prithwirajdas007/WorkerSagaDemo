namespace WorkerSagaDemo.Contracts.Contracts;

/// <summary>
/// Published by the saga whenever a Job's status changes.
/// Subscribers (e.g. the API's SignalR forwarder) receive this and push
/// notifications to connected clients.
///
/// Description, TradeCategory, and RiskTier are optional enrichment fields
/// added in Session D. They carry classification context so the UI can
/// display it without a separate GET /jobs/{id} fetch. Null when not
/// yet classified or when the Job has no description.
/// </summary>
public record JobStatusChanged(
    Guid JobId,
    string Status,
    int CompletedSteps,
    int TotalSteps,
    DateTimeOffset ChangedAt,
    string? Description = null,
    string? TradeCategory = null,
    string? RiskTier = null);
