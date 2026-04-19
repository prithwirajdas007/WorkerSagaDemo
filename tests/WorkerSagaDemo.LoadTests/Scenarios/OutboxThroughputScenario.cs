using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace WorkerSagaDemo.LoadTests.Scenarios;

/// <summary>
/// Measures POST /jobs throughput under concurrent load.
///
/// This is the most portfolio-relevant scenario. POST /jobs opens a
/// Postgres connection, begins a transaction, writes to Marten, writes
/// to the Rebus outbox, and commits. The question being answered:
/// "How many transactional writes per second can the outbox sustain
/// before connection pool exhaustion or latency degradation?"
///
/// The Worker processes jobs at its own pace (parallelism=1). This test
/// intentionally builds a backlog to prove the write path is independent
/// of the processing path. After the load run, we optionally verify all
/// submitted jobs eventually reach Completed status.
///
/// Load profile: ramp from 1 to 20 requests/sec over 30 seconds,
/// hold 20 req/sec for 60 seconds, taper to 0 over 10 seconds. These
/// numbers are tuned for a laptop with local Docker containers, not a
/// production cluster.
/// </summary>
public static class OutboxThroughputScenario
{
    // Shared across the scenario lifetime so we don't churn sockets.
    private static readonly HttpClient HttpClient = new();

    public static ScenarioProps Create(string baseUrl)
    {
        return Scenario.Create("outbox_throughput", async context =>
        {
            var request = Http.CreateRequest("POST", $"{baseUrl}/jobs");
            var response = await Http.Send(HttpClient, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            // Ramp up: gradually increase the per-second inject rate from a
            // low baseline to 20 requests/second over 30 seconds. This
            // reveals the latency curve as load increases.
            Simulation.RampingInject(
                rate: 20,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(30)),

            // Hold: sustain 20 requests/second for 60 seconds.
            // This is the steady-state measurement window.
            Simulation.Inject(
                rate: 20,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(60)),

            // Cool down: taper to 0 over 10 seconds.
            Simulation.RampingInject(
                rate: 0,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(10))
        );
    }
}
