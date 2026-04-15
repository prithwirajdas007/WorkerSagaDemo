using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Rebus.Config;
using Rebus.Persistence.InMem;
using Rebus.RabbitMq;
using Rebus.ServiceProvider;
using WorkerSagaDemo.Worker;
using WorkerSagaDemo.Worker.Ai;
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

// AI service for the saga's Classify step (Session A: smoke test only,
// not yet called by the saga). Registered as a singleton because the
// Kernel is expensive to build and safe to share across threads.
builder.Services.AddSingleton<IJobAiService, OllamaJobAiService>();

var host = builder.Build();

// Startup smoke test for the AI service. Runs once before the Worker
// starts consuming messages, logs success or a warning on failure, and
// NEVER crashes the worker if Ollama is unreachable. The saga's Classify
// step (added in a later session) will degrade gracefully if the service
// is down.
using (var scope = host.Services.CreateScope())
{
    var aiService = scope.ServiceProvider.GetRequiredService<IJobAiService>();
    var aiLogger = scope.ServiceProvider.GetRequiredService<ILogger<OllamaJobAiService>>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    const string SmokeTestTrade = "5Y IRS, USD 50M notional, SOFR vs fixed 4.25%";
    try
    {
        var classification = await aiService.ClassifyTradeAsync(SmokeTestTrade);
        sw.Stop();
        aiLogger.LogInformation(
            "AI classifier smoke test OK in {Elapsed}ms. Input: \"{Input}\" => Category={Category}, RiskTier={RiskTier}, Rationale=\"{Rationale}\"",
            sw.ElapsedMilliseconds,
            SmokeTestTrade,
            classification.Category,
            classification.RiskTier,
            classification.Rationale);
    }
    catch (Exception ex)
    {
        sw.Stop();
        aiLogger.LogWarning(ex,
            "AI classifier smoke test FAILED after {Elapsed}ms. Classifier will be unavailable " +
            "until the AI service is reachable. This is non-fatal; the worker will " +
            "continue to process messages normally.",
            sw.ElapsedMilliseconds);
    }
}

host.Run();
