using Rebus.Sagas;

namespace WorkerSagaDemo.Worker.Sagas;

public class JobProcessingSagaData : ISagaData
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public Guid JobId { get; set; }
    public int TotalSteps { get; set; }
    public int CompletedSteps { get; set; }
    public bool IsComplete { get; set; }

    // Classification results from the AI classifier, populated in
    // Handle(StartJobCommand) before steps are scheduled.
    // Stored as strings (not enums) because saga data is serialized
    // by Rebus/Marten. String serialization is simpler, more robust,
    // and doesn't break if enum values are added later.
    // Null means classification was not attempted (e.g. no Description
    // on the Job). "Unknown" means classification was attempted but
    // either the model couldn't classify or the classifier was down.
    public string? TradeCategory { get; set; }
    public string? RiskTier { get; set; }
    public string? ClassificationRationale { get; set; }
}
