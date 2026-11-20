using System.Text;
using Basi.Core.Models;
using Basi.Module.Translation.Services;

namespace Basi.Tests.Unit;

public class TxtParserTests
{
    private readonly TxtParser _parser = new();
    private static readonly Guid TestNovelId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ------------------------------------------------------------------ //
    //  Parse — happy paths                                                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_ValidUtf8_ReturnsParagraphsAsSegments()
    {
        var text = "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Equal(3, segments.Count);
        Assert.Equal("First paragraph.", segments[0].Content);
        Assert.Equal("Second paragraph.", segments[1].Content);
        Assert.Equal("Third paragraph.", segments[2].Content);
    }

    [Fact]
    public void Parse_AssignsCorrectOrder()
    {
        var text = "Alpha\n\nBeta\n\nGamma";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Equal(0, segments[0].Order);
        Assert.Equal(1, segments[1].Order);
        Assert.Equal(2, segments[2].Order);
    }

    [Fact]
    public void Parse_AssignsStableId_WithCorrectFormat()
    {
        var novelId = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899");
        var text = "Para one\n\nPara two";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, novelId);

        var expectedPrefix = novelId.ToString("N");
        Assert.Equal($"{expectedPrefix}:000000", segments[0].StableId);
        Assert.Equal($"{expectedPrefix}:000001", segments[1].StableId);
    }

    [Fact]
    public void Parse_StableIds_AreUnique()
    {
        var text = "A\n\nB\n\nC\n\nD\n\nE";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);
        var ids = segments.Select(s => s.StableId).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    [Fact]
    public void Parse_EachSegment_HasNovelIdSet()
    {
        var text = "Content here.\n\nMore content.";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.All(segments, s => Assert.Equal(TestNovelId, s.NovelId));
    }

    [Fact]
    public void Parse_EachSegment_HasNonEmptyGuid()
    {
        var text = "Paragraph.";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.All(segments, s => Assert.NotEqual(Guid.Empty, s.Id));
    }

    // ------------------------------------------------------------------ //
    //  Encoding detection                                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_Utf8WithBom_DecodesCorrectly()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = Encoding.UTF8.GetBytes("BOM paragraph.");
        var bytes = bom.Concat(content).ToArray();

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Single(segments);
        Assert.Equal("BOM paragraph.", segments[0].Content);
    }

    [Fact]
    public void Parse_Utf16LeBom_DecodesCorrectly()
    {
        var text = "UTF-16 LE text";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Single(segments);
        Assert.Equal("UTF-16 LE text", segments[0].Content);
    }

    [Fact]
    public void Parse_Utf16BeBom_DecodesCorrectly()
    {
        var text = "UTF-16 BE text";
        var bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Single(segments);
        Assert.Equal("UTF-16 BE text", segments[0].Content);
    }

    // ------------------------------------------------------------------ //
    //  Size validation                                                     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_FileTooLarge_ThrowsArgumentException()
    {
        var oversized = new byte[10 * 1024 * 1024 + 1];

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(oversized, TestNovelId));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ExactlyAtLimit_DoesNotThrow()
    {
        // 10 MB of spaces — no paragraphs → will throw InvalidOperationException, not ArgumentException
        // So we just confirm ArgumentException is NOT thrown for size
        var atLimit = new byte[10 * 1024 * 1024];
        // Fill with valid whitespace so size check passes but content fails
        Array.Fill(atLimit, (byte)' ');
        // Should NOT throw ArgumentException
        Assert.Throws<InvalidOperationException>(() => _parser.Parse(atLimit, TestNovelId));
    }

    // ------------------------------------------------------------------ //
    //  Null / empty inputs                                                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_NullBytes_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Parse(null!, TestNovelId));
    }

    [Fact]
    public void Parse_EmptyContent_ThrowsInvalidOperationException()
    {
        var bytes = Encoding.UTF8.GetBytes("   \n\n   \n\n   ");

        Assert.Throws<InvalidOperationException>(() => _parser.Parse(bytes, TestNovelId));
    }

    // ------------------------------------------------------------------ //
    //  Whitespace and line ending handling                                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_WindowsLineEndings_SplitsCorrectly()
    {
        var text = "First\r\n\r\nSecond\r\n\r\nThird";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void Parse_MixedLineEndings_SplitsCorrectly()
    {
        var text = "Alpha\r\n\r\nBeta\n\nGamma";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void Parse_ContentIsTrimmed()
    {
        var text = "  Leading spaces  \n\n  Trailing spaces  ";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Equal("Leading spaces", segments[0].Content);
        Assert.Equal("Trailing spaces", segments[1].Content);
    }

    [Fact]
    public void Parse_SingleParagraph_ReturnsSingleSegment()
    {
        var text = "Only one paragraph here.";
        var bytes = Encoding.UTF8.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId);

        Assert.Single(segments);
    }
}
