using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace WorkerSagaDemo.LoadTests.Scenarios;

/// <summary>
/// Simulates a realistic mixed workload: POST /jobs to create work,
/// GET /jobs/{id} to check status. Proves the system doesn't deadlock
/// or degrade reads under concurrent write pressure.
///
/// Uses a shared list of created job IDs. Writer scenario POSTs new
/// jobs and stores the returned ID. Reader scenario picks a random
/// existing job ID and GETs its status.
///
/// Load profile: lighter than the outbox scenario. 3 writes/sec + 7
/// reads/sec, sustained for 60 seconds.
/// </summary>
public static class MixedWorkloadScenario
{
    private static readonly List<string> JobIds = new();
    private static readonly object Lock = new();
    private static readonly HttpClient HttpClient = new();

    public static ScenarioProps[] Create(string baseUrl)
    {
        // Writer: creates jobs, parses the response, stores the id for readers.
        // Using a bare HttpClient here (not Http.Send) so we can read the
        // response body without fighting FSharpOption accessors.
        var writer = Scenario.Create("mixed_write", async context =>
        {
            using var httpResponse = await HttpClient.PostAsync($"{baseUrl}/jobs", content: null);

            if (!httpResponse.IsSuccessStatusCode)
                return Response.Fail(statusCode: ((int)httpResponse.StatusCode).ToString());

            var body = await httpResponse.Content.ReadAsStringAsync();
            // Minimal JSON parse for {"id":"<guid>", ...}
            var idKey = "\"id\":\"";
            var start = body.IndexOf(idKey, StringComparison.Ordinal);
            if (start >= 0)
            {
                start += idKey.Length;
                var end = body.IndexOf('"', start);
                if (end > start)
                {
                    var id = body.Substring(start, end - start);
                    lock (Lock) { JobIds.Add(id); }
                }
            }

            return Response.Ok(statusCode: ((int)httpResponse.StatusCode).ToString());
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.Inject(
                rate: 3,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(60))
        );

        // Reader: picks a random known job id and GETs it. No-op (Response.Ok)
        // if no ids have been captured yet so the reader doesn't fail during
        // the first second of the run before writers have produced anything.
        var reader = Scenario.Create("mixed_read", async context =>
        {
            string? jobId;
            lock (Lock)
            {
                if (JobIds.Count == 0)
                    return Response.Ok();

                jobId = JobIds[Random.Shared.Next(JobIds.Count)];
            }

            var request = Http.CreateRequest("GET", $"{baseUrl}/jobs/{jobId}");
            var response = await Http.Send(HttpClient, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.Inject(
                rate: 7,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(60))
        );

        return new[] { writer, reader };
    }
}
