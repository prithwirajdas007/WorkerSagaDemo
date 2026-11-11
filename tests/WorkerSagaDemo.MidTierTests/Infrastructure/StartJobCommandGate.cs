using System.Text;
using Rebus.Messages;
using Rebus.Pipeline;
using WorkerSagaDemo.Contracts.Contracts;

namespace WorkerSagaDemo.MidTierTests.Infrastructure;

/// <summary>
/// A StartJobCommand captured on the Worker's incoming pipeline, straight off
/// the wire: the deserialized command plus the raw transport body, so tests
/// can assert on what was actually published (content and byte size), not
/// just on downstream effects.
/// </summary>
public sealed record CapturedStartJob(
    StartJobCommand Command,
    string BodyJson,
    int BodyByteCount);

/// <summary>
/// Deterministic control point for the window between the API's transaction
/// commit and the Worker's handling of the resulting StartJobCommand.
///
/// The stale-read bug lives in that window: the outbox forwarder, RabbitMQ,
/// and the Worker's consumer all add latency between "state was committed"
/// and "consumer reads state", and any write landing in between is what the
/// consumer sees. Holding the gate freezes a message at the last point before
/// its handler runs, letting a test perform a concurrent write and then
/// release -- no Thread.Sleep, no races.
///
/// We gate the consumer's pipeline rather than the outbox forwarder because
/// (a) the read we are protecting against happens at consume time, so this is
/// the precise control point covering the entire window (forwarder delay AND
/// transport latency), and (b) it uses Rebus's public pipeline-injection API,
/// whereas holding the forwarder means reaching into Rebus.PostgreSql
/// internals that were not designed to be intercepted.
/// </summary>
public sealed class StartJobCommandGate
{
    private readonly object _sync = new();
    private readonly List<CapturedStartJob> _captured = new();
    private TaskCompletionSource<CapturedStartJob>? _arrival;
    private TaskCompletionSource? _release;

    /// <summary>
    /// Every StartJobCommand that has passed through the Worker's pipeline,
    /// gated or not, in arrival order.
    /// </summary>
    public IReadOnlyList<CapturedStartJob> Captured
    {
        get { lock (_sync) return _captured.ToList(); }
    }

    /// <summary>
    /// Arms the gate. The returned task completes when the next
    /// StartJobCommand arrives at the Worker and is held; the message will
    /// not reach the saga until <see cref="Release"/> is called.
    /// </summary>
    public Task<CapturedStartJob> HoldNextAsync()
    {
        lock (_sync)
        {
            _arrival = new TaskCompletionSource<CapturedStartJob>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _arrival.Task;
        }
    }

    /// <summary>Lets the held message proceed to the saga and disarms the gate.</summary>
    public void Release()
    {
        lock (_sync)
        {
            _release?.TrySetResult();
            _release = null;
            _arrival = null;
        }
    }

    internal async Task PassAsync(CapturedStartJob captured)
    {
        Task? held = null;
        lock (_sync)
        {
            _captured.Add(captured);
            if (_release is not null)
            {
                held = _release.Task;
                _arrival?.TrySetResult(captured);
            }
        }

        if (held is not null)
        {
            await held;
        }
    }
}

/// <summary>
/// Rebus incoming pipeline step, injected after deserialization, that hands
/// every StartJobCommand to the <see cref="StartJobCommandGate"/> before the
/// dispatch step invokes the saga.
/// </summary>
public sealed class StartJobGateStep : IIncomingStep
{
    private readonly StartJobCommandGate _gate;

    public StartJobGateStep(StartJobCommandGate gate) => _gate = gate;

    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<Message>();
        if (message.Body is StartJobCommand command)
        {
            var transport = context.Load<TransportMessage>();
            await _gate.PassAsync(new CapturedStartJob(
                command,
                Encoding.UTF8.GetString(transport.Body),
                transport.Body.Length));
        }

        await next();
    }
}
