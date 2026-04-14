// AppHost orchestrates the entire local stack:
//   - Postgres (with persistent volume so jobs survive restarts)
//   - RabbitMQ (with management plugin)
//   - Worker project
//   - API project (pinned to port 5041 so demo.ps1 keeps working)
//
// Run with: dotnet run --project src/WorkerSagaDemo.AppHost
// Aspire dashboard URL is printed in the startup logs (typically http://localhost:1xxxx)

var builder = DistributedApplication.CreateBuilder(args);

// Postgres with persistent volume so data survives AppHost restarts.
// Without WithDataVolume, every restart wipes the database (and your saga
// state, jobs, and outbox table along with it).
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

// Database name 'worker_demo' matches the existing Marten schema and the
// connection string key consumed by API and Worker.
// Resource name "worker-demo" becomes the connection string key.
// The actual PostgreSQL database is named "worker_demo" (underscores are fine
// in Postgres, just not in Aspire resource names).
var workerDb = postgres.AddDatabase("worker-demo", databaseName: "worker_demo");

// RabbitMQ with management plugin enabled (UI accessible from the dashboard).
// Connection string key 'messaging' matches what API and Worker look up.
var rabbitmq = builder.AddRabbitMQ("messaging")
    .WithManagementPlugin();

// Worker -- background processor that hosts the saga
builder.AddProject<Projects.WorkerSagaDemo_Worker>("worker")
    .WithReference(workerDb)
    .WithReference(rabbitmq)
    .WaitFor(workerDb)
    .WaitFor(rabbitmq);

// API -- pin port 5041 so demo.ps1 and the browser SignalR client keep
// working without changes.
var api = builder.AddProject<Projects.WorkerSagaDemo_Api>("api")
    .WithReference(workerDb)
    .WithReference(rabbitmq)
    .WaitFor(workerDb)
    .WaitFor(rabbitmq);

// Pin the existing HTTP endpoint to port 5041 with no proxy.
api.WithEndpoint("http", endpoint =>
{
    endpoint.Port = 5041;
    endpoint.IsProxied = false;
});

builder.Build().Run();
