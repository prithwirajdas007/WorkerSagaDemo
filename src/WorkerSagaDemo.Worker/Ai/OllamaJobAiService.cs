using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Ollama-backed implementation of IJobAiService. Uses Semantic Kernel's
/// Ollama connector to call a local Ollama server with a structured-output
/// prompt and parses the response into a Classification record.
///
/// Constructor builds the Kernel eagerly so configuration errors fail
/// fast at DI resolve time. The actual network call happens in
/// ClassifyTradeAsync and is where reachability failures will manifest.
///
/// Defensive parsing is the heart of this class:
/// - Strips markdown fences (some models wrap output in ```json ... ```)
/// - Tolerates leading/trailing prose around the JSON object
/// - Retries ONCE with a stricter follow-up prompt on parse failure
/// - Maps unrecognised enum values to Unknown rather than failing
/// </summary>
public class OllamaJobAiService : IJobAiService
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly ILogger<OllamaJobAiService> _logger;
    private readonly string _modelId;

    // System prompt is intentionally short. Every token costs latency on
    // CPU. Examples + format spec + escape hatch is enough to coerce
    // a 3B model into structured output most of the time.
    private const string SystemPrompt = @"You are a fixed income / derivatives trade classifier.
Classify the user's trade description into a JSON object with these exact fields:
- category: one of InterestRateSwap, ForeignExchange, CreditDefaultSwap, Equity, Commodity, Other, Unknown
- riskTier: one of Low, Medium, High, Unknown
- rationale: one short sentence explaining your choice

Use Unknown if you cannot confidently classify. Respond with ONLY the JSON object, no markdown fences, no preamble.

Example input: ""5Y IRS USD 100M SOFR vs fixed""
Example output: {""category"":""InterestRateSwap"",""riskTier"":""Medium"",""rationale"":""Standard 5-year USD interest rate swap with moderate duration risk.""}";

    private const string RetryPromptSuffix = @"

Your previous response could not be parsed as JSON. Reply with ONLY the JSON object, nothing else. No markdown, no commentary.";

    // Constructor-injected service is created eagerly so config errors
    // fail at DI resolve time rather than first-call time.
    public OllamaJobAiService(
        IConfiguration configuration,
        ILogger<OllamaJobAiService> logger)
    {
        _logger = logger;

        var endpoint = configuration.GetConnectionString("ollama")
            ?? throw new InvalidOperationException(
                "Missing connection string 'ollama'. Set it in appsettings.Development.json.");

        _modelId = configuration["Ai:Model"]
            ?? throw new InvalidOperationException(
                "Missing configuration 'Ai:Model'. Set it in appsettings.Development.json.");

        var builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(
            modelId: _modelId,
            endpoint: new Uri(endpoint));

        _kernel = builder.Build();
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
    }

    // Test-only constructor that takes an IChatCompletionService directly.
    // Production code uses the public ctor above; tests use this overload
    // to inject a mocked IChatCompletionService and avoid building a real
    // Kernel or touching Ollama. Kernel is left null in the test path
    // because the chat call doesn't dereference it.
    internal OllamaJobAiService(
        IChatCompletionService chat,
        ILogger<OllamaJobAiService> logger,
        string modelId = "test-model")
    {
        _chat = chat;
        _logger = logger;
        _modelId = modelId;
        _kernel = null!; // intentionally not used in test path
    }

    public async Task<Classification> ClassifyTradeAsync(string description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Trade description is required.", nameof(description));

        var stopwatch = Stopwatch.StartNew();

        // Try once with the standard prompt
        var rawResponse = await CallModelAsync(description, retry: false, ct);
        if (TryParseClassification(rawResponse, out var classification, out var parseError))
        {
            stopwatch.Stop();
            _logger.LogDebug(
                "Classification succeeded in {Elapsed}ms (model={Model}): {Category}/{RiskTier}",
                stopwatch.ElapsedMilliseconds, _modelId, classification!.Category, classification.RiskTier);
            return classification;
        }

        _logger.LogWarning(
            "First classification attempt failed parse ({Error}). Retrying with stricter prompt. Raw response: {Raw}",
            parseError, Truncate(rawResponse, 200));

        // Retry once with a stricter follow-up
        var retryResponse = await CallModelAsync(description, retry: true, ct);
        if (TryParseClassification(retryResponse, out classification, out parseError))
        {
            stopwatch.Stop();
            _logger.LogDebug(
                "Classification succeeded on retry in {Elapsed}ms",
                stopwatch.ElapsedMilliseconds);
            return classification!;
        }

        stopwatch.Stop();
        throw new ClassifierParseException(
            $"Model response could not be parsed as Classification after one retry. Last error: {parseError}",
            retryResponse);
    }

    private async Task<string> CallModelAsync(string description, bool retry, CancellationToken ct)
    {
        var systemPrompt = retry ? SystemPrompt + RetryPromptSuffix : SystemPrompt;

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(description);

        try
        {
            var response = await _chat.GetChatMessageContentAsync(
                history,
                kernel: _kernel,
                cancellationToken: ct);
            return response.Content?.Trim() ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            throw new ClassifierUnavailableException(
                $"Ollama provider unreachable: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not ClassifierUnavailableException && ex is not ClassifierParseException)
        {
            // Wrap unexpected exceptions as Unavailable -- they're not parse
            // errors but they're not user-actionable either. Better diagnostic
            // than letting a raw SK exception bubble up.
            throw new ClassifierUnavailableException(
                $"Unexpected error calling AI provider: {ex.Message}", ex);
        }
    }

    private static bool TryParseClassification(string rawResponse, out Classification? classification, out string? error)
    {
        classification = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            error = "empty response";
            return false;
        }

        // Strip markdown code fences if present. Some models wrap JSON in
        // ```json ... ``` despite explicit instructions not to.
        var cleaned = StripMarkdownFences(rawResponse);

        // Find the first { and last } -- some models add a leading or
        // trailing sentence. Extract just the JSON object span.
        var firstBrace = cleaned.IndexOf('{');
        var lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace < firstBrace)
        {
            error = "no JSON object found";
            return false;
        }

        var jsonSpan = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);

        ClassificationDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClassificationDto>(jsonSpan, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"JSON deserialization failed: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "JSON deserialized to null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.Category) || string.IsNullOrWhiteSpace(dto.RiskTier))
        {
            error = "missing required field (category or riskTier)";
            return false;
        }

        // Soft-fail on unrecognised enum values: map to Unknown rather than
        // failing the whole classification.
        var category = ParseEnumOrUnknown<TradeCategory>(dto.Category);
        var riskTier = ParseEnumOrUnknown<RiskTier>(dto.RiskTier);
        var rationale = dto.Rationale ?? string.Empty;

        classification = new Classification(category, riskTier, rationale);
        return true;
    }

    private static T ParseEnumOrUnknown<T>(string value) where T : struct, Enum
    {
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : default;
    }

    private static string StripMarkdownFences(string input)
    {
        // Handle ```json ... ``` and ``` ... ```
        var trimmed = input.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed.Substring(firstNewline + 1);
            if (trimmed.EndsWith("```"))
                trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }
        return trimmed.Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    // DTO for JSON deserialization. Property names use camelCase to match
    // the prompt instructions; JsonNamingPolicy on the options handles it.
    private sealed class ClassificationDto
    {
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("riskTier")] public string? RiskTier { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
