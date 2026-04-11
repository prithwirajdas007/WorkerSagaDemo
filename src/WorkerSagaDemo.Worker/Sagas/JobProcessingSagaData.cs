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
}
