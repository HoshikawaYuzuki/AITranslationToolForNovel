using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.Translation.Providers;
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
    public async Task TranslateBatchAsync_SingleSegmentWithWrongTag_ThrowsBatchExceptionWithUsage()
    {
        // Audit repro F02: AI returned [SEG:99] for a batch containing only segment 0.
        // It must NOT map [SEG:99] to segment 0, and must carry token usage in the exception!
        var fakeProvider = new FakeAiProvider(req => new AiResponse("[SEG:99] unrelated translation", 5, 5, 10, req.Model, FinishReason.Stop));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var ex = await Assert.ThrowsAsync<TranslationBatchException>(() =>
            _engine.TranslateBatchAsync(fakeProvider, "test-model", "en", "vi", batch));

        Assert.Contains("unexpected segment tags", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, ex.TotalTokens);
        Assert.Equal(5, ex.PromptTokens);
        Assert.Equal(5, ex.CompletionTokens);
    }

    [Fact]
    public async Task TranslateBatchAsync_MaxTokensFinishReason_ThrowsTruncatedErrorWithUsage()
    {
        // Audit repro F03: AI returned FinishReason.MaxTokens
        var fakeProvider = new FakeAiProvider(req => new AiResponse("[SEG:0] incomplete translation cut mid", 7, 3, 10, req.Model, FinishReason.MaxTokens));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var ex = await Assert.ThrowsAsync<TranslationBatchException>(() =>
            _engine.TranslateBatchAsync(fakeProvider, "test-model", "en", "vi", batch));

        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, ex.TotalTokens);
        Assert.Equal(7, ex.PromptTokens);
        Assert.Equal(3, ex.CompletionTokens);
    }

    [Fact]
    public async Task TranslateBatchAsync_ErrorFinishReason_ThrowsErrorWithUsage()
    {
        // Audit repro R11: AI returned FinishReason.Error
        var fakeProvider = new FakeAiProvider(req => new AiResponse("[SEG:0] error output", 8, 2, 10, req.Model, FinishReason.Error));

        var batch = new[]
        {
            new SegmentItem(0, "Source 0")
        };

        var ex = await Assert.ThrowsAsync<TranslationBatchException>(() =>
            _engine.TranslateBatchAsync(fakeProvider, "test-model", "en", "vi", batch));

        Assert.Contains("FinishReason", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, ex.TotalTokens);
    }

    [Fact]
    public async Task TranslateBatchAsync_ConsecutiveBatchesWithContext_MockProviderTranslatesSuccessfully()
    {
        var mockProvider = new MockAiProvider(new MockAiProviderOptions { Scenario = MockScenario.Success });

        // Batch 1: segments 0, 1 (no context)
        var batch1 = new[]
        {
            new SegmentItem(0, "Source sentence 0"),
            new SegmentItem(1, "Source sentence 1")
        };

        var result1 = await _engine.TranslateBatchAsync(mockProvider, "mock-v1", "en", "vi", batch1);
        Assert.Equal(2, result1.Translations.Count);
        Assert.True(result1.Translations.ContainsKey(0));
        Assert.True(result1.Translations.ContainsKey(1));

        // Batch 2: segments 2, 3 with context from batch 1
        var context = new[]
        {
            new ContextSegmentItem(0, "Source sentence 0", result1.Translations[0]),
            new ContextSegmentItem(1, "Source sentence 1", result1.Translations[1])
        };
        var batch2 = new[]
        {
            new SegmentItem(2, "Source sentence 2"),
            new SegmentItem(3, "Source sentence 3")
        };

        // F11 Regression check: Must NOT throw "Model returned unexpected segment identifiers"
        var result2 = await _engine.TranslateBatchAsync(mockProvider, "mock-v1", "en", "vi", batch2, context);
        Assert.Equal(2, result2.Translations.Count);
        Assert.True(result2.Translations.ContainsKey(2));
        Assert.True(result2.Translations.ContainsKey(3));
        Assert.False(result2.Translations.ContainsKey(0));
        Assert.False(result2.Translations.ContainsKey(1));
    }
}
