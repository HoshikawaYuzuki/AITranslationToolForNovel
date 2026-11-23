using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.Translation.Services;
using Xunit;

namespace Basi.Tests.Unit;

public class TranslationEngineTests
{
    private readonly TranslationPromptBuilder _promptBuilder = new();
    private readonly TranslationEngine _engine;

    public TranslationEngineTests()
    {
        _engine = new TranslationEngine(_promptBuilder);
    }

    private class FakeAiProvider : IAiProvider
    {
        private readonly Func<AiRequest, AiResponse> _func;

        public FakeAiProvider(Func<AiRequest, AiResponse> func)
        {
            _func = func;
        }

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(_func(request));
        }
    }

    [Fact]
    public async Task TranslateBatchAsync_SuccessfulBatch_ReturnsAllSegmentsAndUsage()
    {
        var batchResponse = "[SEG:0] Bản dịch đoạn 0\n[SEG:1] Bản dịch đoạn 1";
        var fakeProvider = new FakeAiProvider(req => new AiResponse(batchResponse, 10, 15, 25, req.Model, FinishReason.Stop));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0"),
            new SegmentItem(1, "Source 1")
        };

        var result = await _engine.TranslateBatchAsync(
            fakeProvider,
            "test-model",
            "en",
            "vi",
            batch);

        Assert.Equal(2, result.Translations.Count);
        Assert.Equal("Bản dịch đoạn 0", result.Translations[0]);
        Assert.Equal("Bản dịch đoạn 1", result.Translations[1]);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(15, result.CompletionTokens);
        Assert.Equal(25, result.TotalTokens);
        Assert.Equal("test-model", result.ModelUsed);
    }

    [Fact]
    public async Task TranslateBatchAsync_EmptyBatch_ReturnsEmptyDictionary()
    {
        var fakeProvider = new FakeAiProvider(req => new AiResponse("", 0, 0, 0, req.Model, FinishReason.Stop));
        var result = await _engine.TranslateBatchAsync(
            fakeProvider,
            "test-model",
            "en",
            "vi",
            Array.Empty<SegmentItem>());

        Assert.Empty(result.Translations);
        Assert.Equal(0, result.TotalTokens);
    }

    [Fact]
    public async Task TranslateBatchAsync_SingleSegmentUntaggedResponse_FallsBackToRawText()
    {
        // AI returned clean plain text without any [SEG: tag for a single segment batch
        var fakeProvider = new FakeAiProvider(req => new AiResponse("Bản dịch đơn lẻ không có tag", 5, 5, 10, req.Model, FinishReason.Stop));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var result = await _engine.TranslateBatchAsync(
            fakeProvider,
            "test-model",
            "en",
            "vi",
            batch);

        Assert.Single(result.Translations);
        Assert.Equal("Bản dịch đơn lẻ không có tag", result.Translations[0]);
    }

    [Fact]
    public async Task TranslateBatchAsync_SingleSegmentWithWrongTag_DoesNotFallbackToWrongTag()
    {
        // Audit repro F02: AI returned [SEG:99] for a batch containing only segment 0.
        // It must NOT map [SEG:99] to segment 0!
        var fakeProvider = new FakeAiProvider(req => new AiResponse("[SEG:99] unrelated translation", 5, 5, 10, req.Model, FinishReason.Stop));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var result = await _engine.TranslateBatchAsync(
            fakeProvider,
            "test-model",
            "en",
            "vi",
            batch);

        Assert.Empty(result.Translations);
        Assert.False(result.Translations.ContainsKey(0));
    }

    [Fact]
    public async Task TranslateBatchAsync_MaxTokensFinishReason_ThrowsTruncatedError()
    {
        // Audit repro F03: AI returned FinishReason.MaxTokens
        var fakeProvider = new FakeAiProvider(req => new AiResponse("[SEG:0] incomplete translation cut mid", 5, 5, 10, req.Model, FinishReason.MaxTokens));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var ex = await Assert.ThrowsAsync<AiProviderError>(() =>
            _engine.TranslateBatchAsync(fakeProvider, "test-model", "en", "vi", batch));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
