extern alias Worker;

using Worker::WorkerSagaDemo.Worker.Ai;

namespace WorkerSagaDemo.MidTierTests.Infrastructure;

/// <summary>
/// Deterministic stand-in for the Ollama classifier. Records every
/// description it is asked to classify and returns a fixed result
/// immediately, so mid-tier tests neither depend on a local LLM nor pay
/// its latency. The recorded inputs are the assertion surface for the
/// stale-read tests: "what description did the saga actually feed the
/// classifier?"
/// </summary>
public sealed class RecordingJobAiService : IJobAiService
{
    private readonly object _sync = new();
    private readonly List<string> _descriptions = new();
    private TaskCompletionSource<string>? _next;

    /// <summary>Every description passed to the classifier, in call order.</summary>
    public IReadOnlyList<string> ClassifiedDescriptions
    {
        get { lock (_sync) return _descriptions.ToList(); }
    }

    /// <summary>
    /// Completes with the description of the next classification call.
    /// Lets tests await "the saga classified something" without polling.
    /// </summary>
    public Task<string> WaitForNextClassificationAsync()
    {
        lock (_sync)
        {
            _next = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _next.Task;
        }
    }

    public Task<Classification> ClassifyTradeAsync(string description, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _descriptions.Add(description);
            _next?.TrySetResult(description);
            _next = null;
        }

        return Task.FromResult(new Classification(
            TradeCategory.InterestRateSwap,
            RiskTier.Medium,
            "Deterministic mid-tier test classification"));
    }
}
