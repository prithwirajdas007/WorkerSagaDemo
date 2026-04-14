namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Abstraction over the AI model used by the saga. This interface exists so
/// unit tests can mock AI calls without needing a real Ollama instance, and
/// so the underlying provider (Ollama, OpenAI, Bedrock, Azure OpenAI) can
/// be swapped by changing the DI registration without touching the saga.
///
/// Session A scope: only PingAsync is defined. Later sessions add
/// ClassifyTradeAsync and other business methods.
/// </summary>
public interface IJobAiService
{
    /// <summary>
    /// Smoke test -- asks the model to reply with "OK". Returns the actual
    /// response string (which may not literally be "OK" -- models don't always
    /// follow instructions perfectly). Throws if the provider is unreachable
    /// or misconfigured; callers should handle exceptions and degrade
    /// gracefully.
    /// </summary>
    Task<string> PingAsync(CancellationToken ct = default);
}
