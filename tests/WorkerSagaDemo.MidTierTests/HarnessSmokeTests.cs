using System.Text.Json;
using Alba;
using WorkerSagaDemo.MidTierTests.Infrastructure;
using Xunit.Abstractions;

namespace WorkerSagaDemo.MidTierTests;

/// <summary>
/// Proves the mid-tier harness itself: a POST /jobs travels the real path
/// (Marten write + outbox insert in one transaction, outbox forwarder,
/// RabbitMQ, Rebus deserialization, saga dispatch) and ends up at the
/// classifier, with the wire payload captured along the way.
/// </summary>
[Collection(MidTierCollection.Name)]
public class HarnessSmokeTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly MessagingInfrastructure _infra;
    private readonly ITestOutputHelper _output;
    private SagaHarness _harness = null!;

    public HarnessSmokeTests(MessagingInfrastructure infra, ITestOutputHelper output)
    {
        _infra = infra;
        _output = output;
    }

    public async Task InitializeAsync() => _harness = await SagaHarness.StartAsync(_infra);

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Job_flows_from_api_through_outbox_and_rabbitmq_to_the_saga()
    {
        var classified = _harness.Classifier.WaitForNextClassificationAsync();

        var response = await _harness.Api.Scenario(s =>
        {
            s.Post.Url("/jobs");
            s.StatusCodeShouldBe(202);
        });

        var created = response.ReadAsJson<JobAccepted>();
        Assert.NotNull(created);

        // The saga classified exactly the description the API committed.
        var classifiedDescription = await classified.WaitAsync(Timeout);
        Assert.Equal(created!.Description, classifiedDescription);

        // The StartJobCommand was captured off the wire on the Worker side.
        var captured = Assert.Single(_harness.Gate.Captured, c => c.Command.JobId == created.Id);
        Assert.True(captured.BodyByteCount > 0);

        // The wire payload is valid JSON (Rebus serialized it, not us).
        using var _ = JsonDocument.Parse(captured.BodyJson);

        // Document the real wire cost of the enriched payload on every run.
        _output.WriteLine($"StartJobCommand wire payload: {captured.BodyByteCount} bytes");
        _output.WriteLine(captured.BodyJson);
    }

    private sealed record JobAccepted(Guid Id, string Status, string? Description);
}
