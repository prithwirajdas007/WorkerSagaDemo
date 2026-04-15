extern alias Worker;

using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;
using Worker::WorkerSagaDemo.Worker.Ai;

namespace WorkerSagaDemo.Tests.Ai;

/// <summary>
/// Unit tests for OllamaJobAiService. These tests mock IChatCompletionService
/// at the Semantic Kernel level so they're fast, deterministic, and require
/// no Ollama instance.
///
/// What's tested:
/// - Happy path: well-formed JSON parses correctly
/// - Markdown fence stripping
/// - Leading/trailing prose stripping
/// - Soft-fail on unrecognised category/risk tier (maps to Unknown)
/// - Hard fail on missing fields
/// - Hard fail on completely malformed response (after retry)
/// - HttpRequestException becomes ClassifierUnavailableException
/// - Empty/whitespace input rejected with ArgumentException
///
/// What's NOT tested here (saga-level concerns, deferred to Session C):
/// - Retry logic interaction with saga DeferLocal
/// - End-to-end with real Ollama
/// </summary>
public class OllamaJobAiServiceTests
{
    private readonly Mock<IChatCompletionService> _chatMock = new();
    private readonly Mock<ILogger<OllamaJobAiService>> _loggerMock = new();

    private OllamaJobAiService CreateService()
        => new(_chatMock.Object, _loggerMock.Object);

    private void SetupChatResponse(string content)
    {
        var chatMessageContent = new ChatMessageContent(AuthorRole.Assistant, content);
        _chatMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageContent> { chatMessageContent });
    }

    private void SetupChatSequence(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        _chatMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var content = queue.Dequeue();
                return new List<ChatMessageContent>
                {
                    new ChatMessageContent(AuthorRole.Assistant, content)
                };
            });
    }

    [Fact]
    public async Task ClassifyTradeAsync_WellFormedJson_ParsesCorrectly()
    {
        SetupChatResponse(
            "{\"category\":\"InterestRateSwap\",\"riskTier\":\"Medium\",\"rationale\":\"Standard 5Y IRS\"}");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("5Y IRS USD 50M");

        Assert.Equal(TradeCategory.InterestRateSwap, result.Category);
        Assert.Equal(RiskTier.Medium, result.RiskTier);
        Assert.Equal("Standard 5Y IRS", result.Rationale);
    }

    [Fact]
    public async Task ClassifyTradeAsync_StripsMarkdownFences()
    {
        SetupChatResponse(
            "```json\n{\"category\":\"ForeignExchange\",\"riskTier\":\"Low\",\"rationale\":\"Spot FX\"}\n```");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("EURUSD spot 10M");

        Assert.Equal(TradeCategory.ForeignExchange, result.Category);
        Assert.Equal(RiskTier.Low, result.RiskTier);
    }

    [Fact]
    public async Task ClassifyTradeAsync_StripsLeadingProse()
    {
        SetupChatResponse(
            "Sure! Here is the classification:\n{\"category\":\"Equity\",\"riskTier\":\"High\",\"rationale\":\"Single name equity\"}");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("AAPL 1000 shares");

        Assert.Equal(TradeCategory.Equity, result.Category);
        Assert.Equal(RiskTier.High, result.RiskTier);
    }

    [Fact]
    public async Task ClassifyTradeAsync_UnrecognisedCategory_MapsToUnknown()
    {
        SetupChatResponse(
            "{\"category\":\"WeatherDerivative\",\"riskTier\":\"High\",\"rationale\":\"Exotic\"}");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("Snowfall index swap");

        Assert.Equal(TradeCategory.Unknown, result.Category);
        Assert.Equal(RiskTier.High, result.RiskTier);
        Assert.Equal("Exotic", result.Rationale);
    }

    [Fact]
    public async Task ClassifyTradeAsync_UnrecognisedRiskTier_MapsToUnknown()
    {
        SetupChatResponse(
            "{\"category\":\"Commodity\",\"riskTier\":\"Catastrophic\",\"rationale\":\"Oil\"}");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("WTI crude future");

        Assert.Equal(TradeCategory.Commodity, result.Category);
        Assert.Equal(RiskTier.Unknown, result.RiskTier);
    }

    [Fact]
    public async Task ClassifyTradeAsync_MissingCategoryField_RetriesThenThrows()
    {
        SetupChatSequence(
            "{\"riskTier\":\"Medium\",\"rationale\":\"missing category\"}",
            "{\"riskTier\":\"Medium\",\"rationale\":\"still missing\"}");

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<ClassifierParseException>(
            () => service.ClassifyTradeAsync("test"));

        Assert.Contains("missing required field", ex.Message);
        // Verify it actually retried -- the mock should have been called twice
        _chatMock.Verify(
            c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ClassifyTradeAsync_FirstAttemptBadJson_SecondAttemptSucceeds()
    {
        SetupChatSequence(
            "I think it's an interest rate swap but I'm not sure",
            "{\"category\":\"InterestRateSwap\",\"riskTier\":\"Medium\",\"rationale\":\"5Y\"}");

        var service = CreateService();
        var result = await service.ClassifyTradeAsync("5Y IRS");

        Assert.Equal(TradeCategory.InterestRateSwap, result.Category);
        _chatMock.Verify(
            c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ClassifyTradeAsync_BothAttemptsBadJson_ThrowsParseException()
    {
        SetupChatSequence(
            "I cannot classify this",
            "Still cannot classify");

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<ClassifierParseException>(
            () => service.ClassifyTradeAsync("garbage input"));

        Assert.Equal("Still cannot classify", ex.RawResponse);
    }

    [Fact]
    public async Task ClassifyTradeAsync_HttpFailure_ThrowsUnavailable()
    {
        _chatMock
            .Setup(c => c.GetChatMessageContentsAsync(
                It.IsAny<ChatHistory>(),
                It.IsAny<PromptExecutionSettings?>(),
                It.IsAny<Kernel?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = CreateService();
        var ex = await Assert.ThrowsAsync<ClassifierUnavailableException>(
            () => service.ClassifyTradeAsync("5Y IRS"));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ClassifyTradeAsync_EmptyInput_ThrowsArgument()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ClassifyTradeAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ClassifyTradeAsync("   "));
    }
}
