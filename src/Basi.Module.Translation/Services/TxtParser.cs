using System.Text;
using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

/// <summary>
/// Result of parsing a raw text document, including full preserved raw text and individual segments.
/// </summary>
public sealed record TxtParseResult(string RawContent, IReadOnlyList<Segment> Segments);

/// <summary>
/// Parses a raw TXT file into a preserved raw string and an ordered list of <see cref="Segment"/> records.
/// </summary>
public sealed class TxtParser
{
    public const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    static TxtParser()
    {
        // Register CJK and other international code pages (Shift-JIS, GB18030, EUC-KR, Windows-125x)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Parses raw bytes into preserved raw text and segments.
    /// </summary>
    /// <param name="bytes">Raw file bytes.</param>
    /// <param name="novelId">The novel these segments belong to.</param>
    /// <param name="sourceLanguageHint">Optional BCP-47 language tag hint (e.g. "ja", "zh-CN", "ko") for accurate CJK detection.</param>
    /// <returns>A <see cref="TxtParseResult"/> containing normalized raw text and segments.</returns>
    public TxtParseResult ParseFull(byte[] bytes, Guid novelId, string? sourceLanguageHint = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length > MaxFileSizeBytes)
            throw new ArgumentException(
                $"File size {bytes.Length} bytes exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");

        if (bytes.Length == 0)
            throw new InvalidOperationException("The uploaded file is empty.");

        // 1. Try BOM detection first. UTF-16 with BOM contains 50% null bytes for ASCII and is legitimate.
        string? text = TryDetectBom(bytes);

        // 2. If no BOM, check for binary content (null bytes check)
        if (text is null)
        {
            int nullCount = 0;
            int checkLimit = Math.Min(bytes.Length, 8192);
            for (int i = 0; i < checkLimit; i++)
            {
                if (bytes[i] == 0) nullCount++;
            }
            if (nullCount > 0)
                throw new ArgumentException("The file appears to be binary, not plain text.");

            text = DetectAndDecode(bytes, sourceLanguageHint);
        }

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var segments = SplitIntoSegments(normalized, novelId);

        return new TxtParseResult(normalized, segments);
    }

    /// <summary>
    /// Convenience backward-compatible method that returns only segments.
    /// </summary>
    public IReadOnlyList<Segment> Parse(byte[] bytes, Guid novelId, string? sourceLanguageHint = null)
    {
        return ParseFull(bytes, novelId, sourceLanguageHint).Segments;
    }

    // ------------------------------------------------------------------ //
    //  Encoding detection                                                  //
    // ------------------------------------------------------------------ //

    internal static string? TryDetectBom(byte[] bytes)
    {
        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return null;
    }

    internal static string DetectAndDecode(byte[] bytes, string? sourceLanguageHint = null)
    {
        var bomText = TryDetectBom(bytes);
        if (bomText != null) return bomText;

        // Try UTF-8 strict (no fallback chars)
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8, proceed to CJK detection
        }

        // Determine candidate order based on language hint
        var candidates = GetEncodingCandidates(sourceLanguageHint);

        foreach (var name in candidates)
        {
            try
            {
                var enc = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var decoded = enc.GetString(bytes);

                // Reject if decoded text contains unprintable control characters (except common whitespace)
                if (decoded.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                    continue;

                var reEncoded = enc.GetBytes(decoded);
                if (reEncoded.SequenceEqual(bytes))
                    return decoded;
            }
            catch
            {
                // Failed strict decoding, try next candidate
            }
        }

        // Final fallback: UTF-8 with replacement
        return Encoding.UTF8.GetString(bytes);
    }

    private static string[] GetEncodingCandidates(string? sourceLanguageHint)
    {
        if (!string.IsNullOrEmpty(sourceLanguageHint))
        {
            var hint = sourceLanguageHint.Trim().ToLowerInvariant();
            if (hint.StartsWith("zh"))
            {
                if (hint.Contains("tw") || hint.Contains("hk"))
                    return ["big5", "gb18030", "shift_jis", "euc-kr"];
                return ["gb18030", "big5", "shift_jis", "euc-kr"];
            }
            if (hint.StartsWith("ja"))
            {
                return ["shift_jis", "gb18030", "euc-kr", "big5"];
            }
            if (hint.StartsWith("ko"))
            {
                return ["euc-kr", "shift_jis", "gb18030", "big5"];
            }
        }

        return ["shift_jis", "gb18030", "euc-kr", "big5"];
    }

    // ------------------------------------------------------------------ //
    //  Segmentation                                                        //
    // ------------------------------------------------------------------ //

    internal static IReadOnlyList<Segment> SplitIntoSegments(string normalizedText, Guid novelId)
    {
        var paragraphs = normalizedText
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (paragraphs.Count == 0)
            throw new InvalidOperationException("The uploaded file contains no non-empty paragraphs.");

        var novelIdStr = novelId.ToString("N");
        return paragraphs
            .Select((content, index) => new Segment
            {
                Id = Guid.NewGuid(),
                NovelId = novelId,
                StableId = $"{novelIdStr}:{index:D6}",
                Order = index,
                Content = content,
            })
            .ToList();
    }
}
