using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace WorkerSagaDemo.Worker.Ai;

/// <summary>
/// Ollama-backed implementation of IJobAiService. Uses Semantic Kernel's
/// Ollama connector (currently marked experimental via SKEXP0070) to talk
/// to a local Ollama server.
///
/// Constructor builds the Kernel eagerly so that configuration errors fail
/// fast at DI resolve time rather than at first call. The actual network
/// call to Ollama happens inside PingAsync() and is where connectivity
/// failures will manifest.
/// </summary>
public class OllamaJobAiService : IJobAiService
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly ILogger<OllamaJobAiService> _logger;
    private readonly string _modelId;

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

        // Build the kernel with the Ollama connector. The connector is
        // currently experimental -- if the API changes in a future SK
        // version, this is the line to update.
        var builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(
            modelId: _modelId,
            endpoint: new Uri(endpoint));

        _kernel = builder.Build();
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
    }

    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var history = new ChatHistory();
        history.AddSystemMessage(
            "You are a terse assistant. Reply with only the single word OK and nothing else.");
        history.AddUserMessage("ping");

        var response = await _chat.GetChatMessageContentAsync(
            history,
            kernel: _kernel,
            cancellationToken: ct);

        stopwatch.Stop();

        var text = response.Content?.Trim() ?? string.Empty;
        _logger.LogDebug(
            "Ollama ping completed in {Elapsed}ms, response: {Response}",
            stopwatch.ElapsedMilliseconds,
            text);

        return text;
    }
}
