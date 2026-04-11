namespace WorkerSagaDemo.Contracts.Contracts
{
    public record PingMessage(Guid Id, DateTimeOffset CreatedAt);
}
