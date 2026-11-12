extern alias Worker;

using Alba;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rebus.Config;
using Rebus.Persistence.InMem;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.RabbitMq;
using Rebus.ServiceProvider;
using Worker::WorkerSagaDemo.Worker.Ai;
using Worker::WorkerSagaDemo.Worker.Sagas;

namespace WorkerSagaDemo.MidTierTests.Infrastructure;

/// <summary>
/// Boots the real API (via Alba, with the real Marten store, real Rebus bus,
/// and real Postgres outbox + forwarder) and a Worker host running the real
/// JobProcessingSaga against the same Testcontainers infrastructure.
///
/// Two deliberate substitutions, both at the edges rather than the middle:
///  - IJobAiService is a <see cref="RecordingJobAiService"/> so tests do not
///    depend on Ollama and can assert on classifier inputs.
///  - A <see cref="StartJobGateStep"/> is injected into the Worker's incoming
///    pipeline so tests can deterministically hold a message in the window
///    between outbox commit and saga handling.
///
/// Everything between HTTP request and saga handler -- the transactional
/// outbox insert, the forwarder, RabbitMQ, Rebus serialization, saga
/// correlation -- is the production code path.
/// </summary>
public sealed class SagaHarness : IAsyncDisposable
{
    public IAlbaHost Api { get; private set; } = null!;
    public IHost WorkerHost { get; private set; } = null!;
    public StartJobCommandGate Gate { get; } = new();
    public RecordingJobAiService Classifier { get; } = new();

    private SagaHarness() { }

    public static async Task<SagaHarness> StartAsync(MessagingInfrastructure infra)
    {
        var harness = new SagaHarness();

        // Worker first: its RabbitMQ input queue must exist before the API's
        // outbox forwarder publishes the first StartJobCommand, otherwise the
        // message is unroutable and silently dropped.
        harness.WorkerHost = harness.BuildWorkerHost(infra);
        await harness.WorkerHost.StartAsync();

        harness.Api = await AlbaHost.For<Program>(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration) because the API reads
            // these during the WebApplicationBuilder phase: only settings fed
            // into the deferred builder's initial configuration are visible to
            // builder.Configuration.GetConnectionString(...) in Program.cs.
            builder.UseSetting("ConnectionStrings:worker-demo", infra.PostgresConnectionString);
            builder.UseSetting("ConnectionStrings:messaging", infra.RabbitMqConnectionString);
        });

        return harness;
    }

    private IHost BuildWorkerHost(MessagingInfrastructure infra)
    {
        var builder = Host.CreateApplicationBuilder();

        // Mirrors src/WorkerSagaDemo.Worker/Program.cs messaging registration
        // (same transport, same queue name, in-memory sagas and timeouts, one
        // worker, no parallelism). ServiceDefaults/OTel and the Ollama smoke
        // test are intentionally omitted: they are host concerns, not part of
        // the message flow under test.
        builder.Services.AddMarten(options =>
        {
            options.Connection(infra.PostgresConnectionString);
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        builder.Services.AddRebus(configure => configure
            .Transport(t => t.UseRabbitMq(infra.RabbitMqConnectionString, "worker-saga-demo-worker"))
            .Sagas(s => s.StoreInMemory())
            .Timeouts(t => t.StoreInMemory())
            .Options(o =>
            {
                o.SetNumberOfWorkers(1);
                o.SetMaxParallelism(1);
                o.Decorate<IPipeline>(c => new PipelineStepInjector(c.Get<IPipeline>())
                    .OnReceive(
                        new StartJobGateStep(Gate),
                        PipelineRelativePosition.After,
                        typeof(DeserializeIncomingMessageStep)));
            }));

        builder.Services.AddRebusHandler<JobProcessingSaga>();
        builder.Services.AddSingleton<IJobAiService>(Classifier);

        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        // Release any held message so a failed test does not deadlock teardown.
        Gate.Release();

        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        if (WorkerHost is not null)
        {
            await WorkerHost.StopAsync(TimeSpan.FromSeconds(10));
            WorkerHost.Dispose();
        }
    }
}
