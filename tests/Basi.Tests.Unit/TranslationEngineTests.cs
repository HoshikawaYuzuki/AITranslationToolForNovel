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
    public async Task TranslateBatchAsync_SuccessfulBatch_ReturnsAllSegments()
    {
        var batchResponse = "[SEG:0] Bản dịch đoạn 0\n[SEG:1] Bản dịch đoạn 1";
        var fakeProvider = new FakeAiProvider(req => new AiResponse(batchResponse, 10, 10, 20, req.Model, FinishReason.Stop));

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

        Assert.Equal(2, result.Count);
        Assert.Equal("Bản dịch đoạn 0", result[0]);
        Assert.Equal("Bản dịch đoạn 1", result[1]);
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

        Assert.Empty(result);
    }

    [Fact]
    public async Task TranslateBatchAsync_SingleSegmentUntaggedResponse_FallsBackToRawText()
    {
        // AI returned plain text without [SEG:0] tag for a single segment batch
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

        Assert.Single(result);
        Assert.Equal("Bản dịch đơn lẻ không có tag", result[0]);
    }
}
