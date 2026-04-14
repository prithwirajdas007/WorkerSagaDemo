using JasperFx;
using Marten;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Rebus.Config;
using Rebus.Persistence.InMem;
using Rebus.RabbitMq;
using Rebus.ServiceProvider;
using WorkerSagaDemo.Worker;
using WorkerSagaDemo.Worker.Handlers;

var builder = Host.CreateApplicationBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery.
// Works on the generic host builder via the AddServiceDefaults extension
// from WorkerSagaDemo.ServiceDefaults.
builder.AddServiceDefaults();

// Extend the OpenTelemetry tracer with our custom saga ActivitySource.
// ServiceDefaults's ConfigureOpenTelemetry registers the ambient application
// name as a source; we need to also register "WorkerSagaDemo.Saga" so spans
// emitted by SagaTelemetry.ActivitySource are captured by the OTLP exporter
// and shown in the Aspire dashboard Traces tab.
//
// Without this line, StartActivity() calls in the saga return null and no
// spans are recorded -- silent failure, which is why this registration is
// critical.
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
{
    tracing.AddSource(SagaTelemetry.ActivitySourceName);
});

// Connection strings come from configuration:
//   - Under Aspire, AppHost injects ConnectionStrings__worker_demo and
//     ConnectionStrings__messaging environment variables.
//   - Standalone, they fall back to appsettings.Development.json values.
var pgConnectionString = builder.Configuration.GetConnectionString("worker-demo")
    ?? throw new InvalidOperationException(
        "Missing connection string 'worker-demo'. Set it in appsettings.Development.json " +
        "or run via the Aspire AppHost project.");

var rabbitConnectionString = builder.Configuration.GetConnectionString("messaging")
    ?? throw new InvalidOperationException(
        "Missing connection string 'messaging'. Set it in appsettings.Development.json " +
        "or run via the Aspire AppHost project.");

// Marten: same database as the API
builder.Services.AddMarten(options =>
{
    options.Connection(pgConnectionString);
    options.AutoCreateSchemaObjects = AutoCreate.All;
});

// Rebus: worker consumer with in-memory saga storage and timeout storage
builder.Services.AddRebus(configure => configure
    .Transport(t => t.UseRabbitMq(rabbitConnectionString, "worker-saga-demo-worker"))
    .Sagas(s => s.StoreInMemory())
    .Timeouts(t => t.StoreInMemory())
    .Options(o =>
    {
        o.SetNumberOfWorkers(1);
        o.SetMaxParallelism(1);
    })
);

builder.Services.AutoRegisterHandlersFromAssemblyOf<PingMessageHandler>();

var host = builder.Build();
host.Run();
