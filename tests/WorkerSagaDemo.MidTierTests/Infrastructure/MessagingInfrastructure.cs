using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace WorkerSagaDemo.MidTierTests.Infrastructure;

/// <summary>
/// Shared Docker infrastructure for the mid-tier tests: one PostgreSQL 16
/// container and one RabbitMQ container, started once per test collection
/// and torn down when the collection finishes.
///
/// This is the layer the unit tests fake and the load tests assume is
/// already running: here it is real, disposable, and owned by the tests,
/// so assertions can be made about actual outbox rows, actual transport
/// payloads, and actual saga behavior.
/// </summary>
public sealed class MessagingInfrastructure : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("worker_demo")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3-management")
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Start both containers concurrently; combined cold start is the
        // slower of the two rather than the sum.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask());
    }
}

/// <summary>
/// All mid-tier test classes join this collection so they share the two
/// containers and run sequentially (same-collection classes never run in
/// parallel in xUnit). Sequential execution matters because every harness
/// instance uses the same well-known queue names as production.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MidTierCollection : ICollectionFixture<MessagingInfrastructure>
{
    public const string Name = "mid-tier";
}
