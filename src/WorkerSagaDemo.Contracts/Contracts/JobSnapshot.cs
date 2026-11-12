namespace WorkerSagaDemo.Contracts.Contracts;

/// <summary>
/// Point-in-time snapshot of a Job as it was committed, composed inside the
/// same transaction as the state change and carried on the wire. It reads as
/// "this is what was true at CapturedAt", never as "go look it up".
///
/// This is a deliberate contract, NOT the Job domain document serialized onto
/// the wire. It carries exactly what the consumer (the saga) needs -- the
/// description to classify and the step names to schedule -- and nothing
/// else. Status, timestamps, and classification results are omitted on
/// purpose: putting the internal document shape on the wire would freeze the
/// database model into a public contract, and every future document change
/// would become a breaking change for every consumer.
///
/// Immutability: init-only members, a read-only list, and no navigation back
/// to the document. Once composed, nothing re-reads anything -- that is the
/// entire correctness argument.
/// </summary>
public sealed record JobSnapshot
{
    /// <summary>Schema version consumers can dispatch on; see CurrentSchemaVersion.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Free-text trade description as committed, input to classification.</summary>
    public required string? Description { get; init; }

    /// <summary>Ordered names of the processing steps defined at creation.</summary>
    public required IReadOnlyList<string> StepNames { get; init; }

    /// <summary>When this snapshot was composed (inside the creating transaction).</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// The version producers stamp on new snapshots. Bump when the shape
    /// changes; consumers seeing a newer version than they understand should
    /// fail loudly rather than guess.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
