namespace WorkerSagaDemo.Contracts.Domain;

public class Job
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "Queued";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<JobStep> Steps { get; set; } = new();
}

public class JobStep
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
