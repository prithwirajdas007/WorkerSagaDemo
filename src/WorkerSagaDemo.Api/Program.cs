using JasperFx;
using Marten;
using Marten.Services;
using Npgsql;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Config.Outbox;
using Rebus.PostgreSql.Outbox;
using Rebus.RabbitMq;
using Rebus.Routing.TypeBased;
using Rebus.ServiceProvider;
using Rebus.Transport;
using WorkerSagaDemo.Contracts.Contracts;
using WorkerSagaDemo.Contracts.Domain;

var builder = WebApplication.CreateBuilder(args);

const string ConnectionString =
    "Host=localhost;Port=5435;Database=worker_demo;Username=postgres;Password=postgres";

builder.Services.AddOpenApi();

// Marten: document store backed by PostgreSQL
builder.Services.AddMarten(options =>
{
    options.Connection(ConnectionString);
    options.AutoCreateSchemaObjects = AutoCreate.All;
});

// Rebus: message bus with Postgres-backed transactional outbox.
// Messages sent inside a RebusTransactionScope with UseOutbox() are stored in
// the outbox table and forwarded to RabbitMQ by a background sender. If
// RabbitMQ is unreachable, the message survives in Postgres and is delivered
// once connectivity is restored.
builder.Services.AddRebus(configure => configure
    .Transport(t => t.UseRabbitMq(
        "amqp://guest:guest@localhost:5675",
        "worker-saga-demo-api"
    ))
    .Outbox(o => o.StoreInPostgreSql(ConnectionString, "rebus_outbox"))
    .Routing(r => r.TypeBased()
        .Map<PingMessage>("worker-saga-demo-worker")
        .Map<StartJobCommand>("worker-saga-demo-worker")
    )
);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Ping endpoint (not transactional, for connectivity checks)
app.MapPost("/ping", async (IBus bus) =>
{
    var id = Guid.NewGuid();
    await bus.Send(new PingMessage(id, DateTimeOffset.UtcNow));
    return Results.Accepted(value: new { id });
});

// Create a Job, store in Marten, send command to Worker -- transactionally.
// Both the Marten write and the Rebus outbox insert happen inside the same
// Npgsql transaction, so either both succeed or neither does.
app.MapPost("/jobs", async (IBus bus, IDocumentStore store) =>
{
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // Open a Marten session enlisted in our transaction
    var session = store.LightweightSession(Marten.Services.SessionOptions.ForTransaction(tx));
    await using var _ = session;

    var job = new Job
    {
        Id = Guid.NewGuid(),
        Status = "Queued",
        CreatedAt = DateTimeOffset.UtcNow,
        Steps = new List<JobStep>
        {
            new() { Name = "Validate" },
            new() { Name = "Process" },
            new() { Name = "Finalize" }
        }
    };

    session.Store(job);
    await session.SaveChangesAsync();

    // Enlist Rebus in the same Npgsql transaction via the outbox
    using var rebusScope = new RebusTransactionScope();
    rebusScope.UseOutbox(conn, tx);

    await bus.Send(new StartJobCommand(job.Id));

    await rebusScope.CompleteAsync();
    await tx.CommitAsync();

    return Results.Accepted(value: new { job.Id, job.Status });
});

// Read a Job back
app.MapGet("/jobs/{id:guid}", async (Guid id, IQuerySession session) =>
{
    var job = await session.LoadAsync<Job>(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.Run();

// Required for Alba integration tests
public partial class Program { }
