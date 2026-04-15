namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Thrown when the AI provider is unreachable -- network error, connection
/// refused, timeout, etc. This is a transient failure: the caller should
/// retry later or degrade gracefully.
///
/// Distinct from ClassifierParseException because the response shape is
/// fundamentally different: this means the model was never called, that
/// means the model was called and returned garbage.
/// </summary>
public class ClassifierUnavailableException : Exception
{
    public ClassifierUnavailableException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Thrown when the AI provider responded but the response could not be
/// parsed as a Classification -- malformed JSON, missing required fields,
/// etc. This is NOT thrown for unrecognised enum values; those are mapped
/// to Unknown.
///
/// Soft retry has already been attempted before this exception is thrown.
/// The caller should treat this as a permanent failure for this specific
/// input.
/// </summary>
public class ClassifierParseException : Exception
{
    /// <summary>
    /// The raw text the model returned, for diagnostic logging.
    /// </summary>
    public string RawResponse { get; }

    public ClassifierParseException(string message, string rawResponse)
        : base(message)
    {
        RawResponse = rawResponse;
    }

    public ClassifierParseException(string message, string rawResponse, Exception inner)
        : base(message, inner)
    {
        RawResponse = rawResponse;
    }
}
