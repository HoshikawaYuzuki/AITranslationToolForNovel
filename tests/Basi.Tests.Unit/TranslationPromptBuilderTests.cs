using Basi.Module.Translation.Services;
using Xunit;

namespace Basi.Tests.Unit;

public class TranslationPromptBuilderTests
{
    private readonly TranslationPromptBuilder _builder = new();

    [Fact]
    public void BuildPrompt_ContainsLanguageTags()
    {
        var segments = new[] { new SegmentItem(0, "テスト") };
        var prompt = _builder.BuildPrompt("ja", "vi", segments);
        Assert.Contains("ja", prompt.SystemPrompt);
        Assert.Contains("vi", prompt.SystemPrompt);
        Assert.Contains("literary", prompt.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrompt_IncludesSegmentsAndTags()
    {
        var segments = new[]
        {
            new SegmentItem(0, "吾輩は猫である。"),
            new SegmentItem(1, "名前はまだ無い。")
        };

        var prompt = _builder.BuildPrompt("ja", "vi", segments);

        Assert.Contains("[SEG:0] 吾輩は猫である。", prompt.UserPrompt);
        Assert.Contains("[SEG:1] 名前はまだ無い。", prompt.UserPrompt);
        Assert.DoesNotContain("RECENT CONTEXT", prompt.UserPrompt);
    }

    [Fact]
    public void BuildPrompt_IncludesContextHistoryWhenProvided()
    {
        var context = new[]
        {
            new ContextSegmentItem(0, "古い池や", "Ao cũ")
        };
        var segments = new[]
        {
            new SegmentItem(1, "蛙飛びこむ水の音")
        };

        var prompt = _builder.BuildPrompt("ja", "vi", segments, context);

        Assert.Contains("RECENT CONTEXT", prompt.UserPrompt);
        Assert.Contains("[SEG:0] Ao cũ", prompt.UserPrompt);
        Assert.Contains("[SEG:1] 蛙飛びこむ水の音", prompt.UserPrompt);
    }

    [Fact]
    public void ParseResponse_ExtractsTaggedSegmentsAccurately()
    {
        var response = """
            Here is the literary translation:
            [SEG:0] Tôi là một con mèo.
            [SEG:1] Tên thì vẫn chưa có.
            """;

        var expectedOrders = new[] { 0, 1 };
        var parsed = _builder.ParseResponse(response, expectedOrders);

        Assert.True(parsed.ContainsKey(0));
        Assert.True(parsed.ContainsKey(1));
        Assert.Equal("Tôi là một con mèo.", parsed[0]);
        Assert.Equal("Tên thì vẫn chưa có.", parsed[1]);
    }

    [Fact]
    public void ParseResponse_HandlesMultilineSegmentContent()
    {
        var response = """
            [SEG:0]
            Đoạn văn dài
            xuống dòng
            tiếp tục.
            [SEG:1] Đoạn tiếp theo.
            """;

        var parsed = _builder.ParseResponse(response, new[] { 0, 1 });

        Assert.Equal("Đoạn văn dài\nxuống dòng\ntiếp tục.", parsed[0].Replace("\r\n", "\n"));
        Assert.Equal("Đoạn tiếp theo.", parsed[1]);
    }

    [Fact]
    public void ParseResponse_MissingSegment_ReturnsOnlyFound()
    {
        var response = "[SEG:0] Chỉ dịch được câu 0";
        var parsed = _builder.ParseResponse(response, new[] { 0, 1 });

        Assert.Single(parsed);
        Assert.Equal("Chỉ dịch được câu 0", parsed[0]);
        Assert.False(parsed.ContainsKey(1));
    }

    [Fact]
    public void ParseResponse_DuplicateTag_ThrowsInvalidOperationException()
    {
        // Audit repro case: duplicate tag [SEG:0]
        var response = "[SEG:0] first [SEG:0] second [SEG:99] unknown";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _builder.ParseResponse(response, new[] { 0 }));

        Assert.Contains("duplicate segment tags", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public void ParseResponse_UnexpectedTag_ThrowsInvalidOperationException()
    {
        // Segment 99 should be strictly rejected as contract violation when batch expected only 0 and 1
        var response = "[SEG:0] Câu 0 [SEG:99] Bất ngờ [SEG:1] Câu 1";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _builder.ParseResponse(response, new[] { 0, 1 }));

        Assert.Contains("unexpected segment tags", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", ex.Message);
    }

    [Theory]
    [InlineData("[SEG:0] text [SEG:abc] bad", "malformed segment tag")]
    [InlineData("[SEG:0] text [SEG:] empty tag", "malformed segment tag")]
    [InlineData("[SEG:0] text [SEG:-1] negative tag", "malformed segment tag")]
    [InlineData("[SEG:0] text [SEG:999999999999999999999999999999] overflow tag", "malformed segment tag")]
    public void ParseResponse_MalformedTag_ThrowsInvalidOperationException(string response, string expectedErrorMessage)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _builder.ParseResponse(response, new[] { 0 }));

        Assert.Contains(expectedErrorMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
