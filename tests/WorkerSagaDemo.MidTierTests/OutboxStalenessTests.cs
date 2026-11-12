using Alba;
using WorkerSagaDemo.MidTierTests.Infrastructure;

namespace WorkerSagaDemo.MidTierTests;

/// <summary>
/// Demonstrates the difference between a thin outbox event (an ID the
/// consumer resolves later) and an enriched snapshot (payload composed inside
/// the same transaction as the state change).
///
/// Timeline under test:
///   T0 -- POST /jobs commits the Job document and the outbox row atomically.
///         The event means "job created as of T0".
///   T1 -- PATCH /jobs/{id} amends the description while the StartJobCommand
///         is in flight (held at the Worker by the test gate).
///   T2 -- the Worker handles StartJobCommand and feeds the classifier.
///
/// The invariant: the saga must process the state that was committed with the
/// event (T0), not whatever the document happens to say by the time the
/// message is consumed (T1). A thin event cannot satisfy this -- the
/// consumer's re-read is a second read of a moving target.
/// </summary>
[Collection(MidTierCollection.Name)]
public class OutboxStalenessTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly MessagingInfrastructure _infra;
    private SagaHarness _harness = null!;

    public OutboxStalenessTests(MessagingInfrastructure infra) => _infra = infra;

    public async Task InitializeAsync() => _harness = await SagaHarness.StartAsync(_infra);

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Saga_classifies_the_description_committed_with_the_event_not_a_later_amendment()
    {
        // Arm the gate BEFORE creating the job: the next StartJobCommand to
        // reach the Worker will be held just before saga dispatch.
        var held = _harness.Gate.HoldNextAsync();
        var classified = _harness.Classifier.WaitForNextClassificationAsync();

        // T0: create the job. Description A is committed atomically with the
        // outbox row that produces StartJobCommand.
        var response = await _harness.Api.Scenario(s =>
        {
            s.Post.Url("/jobs");
            s.StatusCodeShouldBe(202);
        });
        var created = response.ReadAsJson<JobAccepted>();
        Assert.NotNull(created);
        var descriptionAtCommit = created!.Description;
        Assert.False(string.IsNullOrWhiteSpace(descriptionAtCommit));

        // The command has left the outbox, crossed RabbitMQ, and is now held
        // at the Worker -- deterministically inside the window.
        await held.WaitAsync(Timeout);

        // T1: an amendment lands in the window.
        const string amendedDescription =
            "AMENDED: 10Y USD payer swaption, USD 250M notional, strike 5.00%";
        await _harness.Api.Scenario(s =>
        {
            s.Patch.Json(new { description = amendedDescription }).ToUrl($"/jobs/{created.Id}");
            s.StatusCodeShouldBe(200);
        });

        // T2: release. The saga now handles StartJobCommand.
        _harness.Gate.Release();
        var classifiedDescription = await classified.WaitAsync(Timeout);

        // The event meant "job created as of T0", so the saga must classify
        // description A. With a thin event the saga re-reads the document and
        // sees the T1 amendment instead -- stale enrichment, and this fails.
        Assert.Equal(descriptionAtCommit, classifiedDescription);

        // And the guarantee must hold on the wire, not just in behavior: the
        // published payload itself carries what was true at commit. A thin
        // event (JobId only) fails here; a snapshot composed inside the
        // POST /jobs transaction passes.
        var captured = Assert.Single(_harness.Gate.Captured, c => c.Command.JobId == created.Id);
        Assert.Contains(descriptionAtCommit!, captured.BodyJson);
        Assert.DoesNotContain(amendedDescription, captured.BodyJson);
    }

    private sealed record JobAccepted(Guid Id, string Status, string? Description);
}
