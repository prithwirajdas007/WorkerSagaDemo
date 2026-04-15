namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Abstraction over the AI model used by the saga. This interface exists so
/// unit tests can mock AI calls without needing a real Ollama instance, and
/// so the underlying provider (Ollama, OpenAI, Bedrock, Azure OpenAI) can
/// be swapped by changing the DI registration without touching the saga.
///
/// Session B scope: ClassifyTradeAsync is the only method. PingAsync from
/// Session A is removed -- the startup smoke test now calls ClassifyTradeAsync
/// with a hardcoded example trade, which exercises the same connectivity
/// path plus the parsing logic.
/// </summary>
public interface IJobAiService
{
    /// <summary>
    /// Classifies a free-text trade description into a structured
    /// Classification. The implementation prompts the model to return
    /// JSON matching the Classification shape and parses defensively.
    ///
    /// Failure modes:
    /// - Provider unreachable: throws ClassifierUnavailableException
    /// - Response not parseable as JSON (after one retry): throws
    ///   ClassifierParseException
    /// - JSON missing required fields: throws ClassifierParseException
    /// - Category or RiskTier not in enum: returns Unknown, NOT an exception
    ///
    /// Callers should catch ClassifierUnavailableException and degrade
    /// gracefully (e.g. mark the step as needing manual classification).
    /// ClassifierParseException is generally a model quality issue and
    /// indicates a bad input or a model that can't follow the prompt.
    /// </summary>
    /// <param name="description">Free-text trade description, e.g.
    /// "5Y IRS, USD 50M notional, SOFR vs fixed 4.25%"</param>
    /// <param name="ct">Cancellation token</param>
    Task<Classification> ClassifyTradeAsync(string description, CancellationToken ct = default);
}
