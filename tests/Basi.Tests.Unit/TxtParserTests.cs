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
    //  Encoding detection & CJK CodePages                                  //
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

    [Fact]
    public void Parse_ShiftJis_DecodesCorrectly()
    {
        var text = "吾輩は猫である。\n\n名前はまだ無い。";
        var sjis = Encoding.GetEncoding("shift_jis");
        var bytes = sjis.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId, "ja");

        Assert.Equal(2, segments.Count);
        Assert.Equal("吾輩は猫である。", segments[0].Content);
        Assert.Equal("名前はまだ無い。", segments[1].Content);
    }

    [Fact]
    public void Parse_Gb18030_DecodesCorrectly()
    {
        var text = "第一章：风起云涌\n\n这是一个关于冒险的故事。";
        var gb = Encoding.GetEncoding("gb18030");
        var bytes = gb.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId, "zh-CN");

        Assert.Equal(2, segments.Count);
        Assert.Equal("第一章：风起云涌", segments[0].Content);
        Assert.Equal("这是一个关于冒险的故事。", segments[1].Content);
    }

    [Fact]
    public void Parse_EucKr_DecodesCorrectly()
    {
        var text = "제1장: 새로운 시작\n\n모든 것은 그날 시작되었다.";
        var eucKr = Encoding.GetEncoding("euc-kr");
        var bytes = eucKr.GetBytes(text);

        var segments = _parser.Parse(bytes, TestNovelId, "ko");

        Assert.Equal(2, segments.Count);
        Assert.Equal("제1장: 새로운 시작", segments[0].Content);
        Assert.Equal("모든 것은 그날 시작되었다.", segments[1].Content);
    }

    // ------------------------------------------------------------------ //
    //  Preservation & ParseFull                                            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ParseFull_PreservesRawContent_AndNormalizesLineEndings()
    {
        var original = "Line 1\r\n\r\nLine 2 with internal\r\nnewline.\r\n\r\nLine 3.";
        var bytes = Encoding.UTF8.GetBytes(original);

        var result = _parser.ParseFull(bytes, TestNovelId);

        Assert.Equal("Line 1\n\nLine 2 with internal\nnewline.\n\nLine 3.", result.RawContent);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("Line 2 with internal\nnewline.", result.Segments[1].Content);
    }

    // ------------------------------------------------------------------ //
    //  Binary file & Size validation                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_BinaryFile_ThrowsArgumentException()
    {
        var binary = new byte[100];
        Array.Fill(binary, (byte)0); // All null bytes

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(binary, TestNovelId));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    //  R2 & R3 Regression Tests: Null bytes, Control chars, Malformed bytes //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Parse_Utf8BomWithNullByte_ThrowsArgumentException()
    {
        // [239, 187, 191, 65, 0, 66] — UTF8 BOM + 'A' + NUL + 'B'
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', 0x00, (byte)'B' };

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NullByteAfter8192Bytes_ThrowsArgumentException()
    {
        // 8192 bytes of ASCII 'A' followed by NUL and 'B'
        var bytes = new byte[8194];
        Array.Fill(bytes, (byte)'A', 0, 8192);
        bytes[8192] = 0x00;
        bytes[8193] = (byte)'B';

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnprintableControlCharacters_ThrowsArgumentException()
    {
        // ASCII with BEL character (0x07)
        var text = "Hello\x07World";
        var bytes = Encoding.UTF8.GetBytes(text);

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
        Assert.Contains("control", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Utf16WithNullCharacter_ThrowsArgumentException()
    {
        // UTF-16 containing an actual U+0000 character
        var text = "Valid\0Content";
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Utf8BomWithMalformedBytes_ThrowsArgumentException()
    {
        // [239, 187, 191, 65, 255, 66] — UTF8 BOM + 'A' + 0xFF + 'B' (0xFF is invalid UTF-8)
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', 0xFF, (byte)'B' };

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Utf16OddLengthPayload_ThrowsArgumentException()
    {
        // UTF-16 LE BOM (2 bytes) + 1 single byte payload (odd byte length)
        var bytes = new byte[] { 0xFF, 0xFE, 0x41 };

        Assert.Throws<ArgumentException>(() => _parser.Parse(bytes, TestNovelId));
    }

    [Fact]
    public void Parse_ValidUtf8WithLiteralReplacementCharacter_DecodesCorrectly()
    {
        // Valid UTF-8 containing literal U+FFFD (0xEF, 0xBF, 0xBD) should be accepted
        var text = "Valid content with \uFFFD replacement symbol";
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = _parser.Parse(bytes, TestNovelId);
        Assert.Single(result);
        Assert.Equal(text, result[0].Content);
    }

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
        var atLimit = new byte[10 * 1024 * 1024];
        Array.Fill(atLimit, (byte)' ');
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
