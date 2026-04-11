using Marten;
using Rebus.Bus;
using Rebus.Config;
using Rebus.RabbitMq;
using Rebus.Routing.TypeBased;
using Rebus.ServiceProvider;
using WorkerSagaDemo.Contracts.Contracts;
using WorkerSagaDemo.Contracts.Domain;
using JasperFx;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Marten: document store backed by PostgreSQL
builder.Services.AddMarten(options =>
{
    options.Connection("Host=localhost;Port=5435;Database=worker_demo;Username=postgres;Password=postgres");
    options.AutoCreateSchemaObjects = AutoCreate.All;
});

// Rebus: message bus
builder.Services.AddRebus(configure => configure
    .Transport(t => t.UseRabbitMq(
        "amqp://guest:guest@localhost:5675",
        "worker-saga-demo-api"
    ))
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

// Keep old ping endpoint
app.MapPost("/ping", async (IBus bus) =>
{
    var id = Guid.NewGuid();
    await bus.Send(new PingMessage(id, DateTimeOffset.UtcNow));
    return Results.Accepted(value: new { id });
});

// Create a Job, store in Marten, send command to Worker
app.MapPost("/jobs", async (IBus bus, IDocumentSession session) =>
{
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
    await bus.Send(new StartJobCommand(job.Id));

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
