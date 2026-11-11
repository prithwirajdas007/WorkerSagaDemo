namespace WorkerSagaDemo.Contracts.Contracts;

/// <summary>
/// Starts the job processing saga. Carries a <see cref="JobSnapshot"/>
/// composed in the same transaction that created the Job, so the consumer
/// processes what was true at commit time instead of re-reading a document
/// that may have been amended while the message was in flight.
/// </summary>
public record StartJobCommand(Guid JobId, JobSnapshot Snapshot);
