namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Categories the classifier can assign to a trade description.
///
/// Unknown is the soft-fail value: returned when the LLM produces a
/// category string that doesn't map to any of the other enum values.
/// Saga code should treat Unknown as "needs manual review" rather
/// than as an error condition.
///
/// Six values (excluding Unknown) is the upper bound for reliable
/// classification with a 3B parameter local model. Adding more
/// categories would noticeably degrade accuracy.
/// </summary>
public enum TradeCategory
{
    Unknown,
    InterestRateSwap,
    ForeignExchange,
    CreditDefaultSwap,
    Equity,
    Commodity,
    Other
}

/// <summary>
/// Risk tiers the classifier can assign to a trade.
///
/// Unknown follows the same soft-fail semantics as TradeCategory.Unknown:
/// returned when the model produces an unrecognised value, not when the
/// model fails to respond. (Unreachable model = exception, malformed
/// content = ClassifierParseException, unrecognised enum value = Unknown.)
/// </summary>
public enum RiskTier
{
    Unknown,
    Low,
    Medium,
    High
}

/// <summary>
/// Structured output of the AI classifier.
///
/// Category and RiskTier are enums for type safety and saga branching.
/// Rationale is free text because forcing structured output for
/// explanations doesn't improve quality and wastes tokens.
/// </summary>
public record Classification(
    TradeCategory Category,
    RiskTier RiskTier,
    string Rationale);
