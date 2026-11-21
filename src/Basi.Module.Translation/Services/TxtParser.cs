using System.Text;
using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

/// <summary>
/// Result of parsing a raw text document, including preserved normalized raw text and individual segments.
/// </summary>
public sealed record TxtParseResult(string RawContent, IReadOnlyList<Segment> Segments);

/// <summary>
/// Parses a raw TXT file into a preserved raw string and an ordered list of <see cref="Segment"/> records.
/// Enforces strict character validation (no silent U+FFFD replacement, no NUL characters, no unprintable control characters).
/// </summary>
public sealed class TxtParser
{
    public const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    static TxtParser()
    {
        // Register CJK and other international code pages (Shift-JIS, GB18030, EUC-KR, Big5)
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

        // Decode with strict validation (throws on malformed bytes, no silent U+FFFD replacement)
        var text = DetectAndDecode(bytes, sourceLanguageHint);

        // Validate decoded characters across ALL code paths:
        // 1. Reject NUL characters
        if (text.Contains('\0'))
            throw new ArgumentException("The file contains invalid null characters (NUL).");

        // 2. Reject unprintable control characters (only \r, \n, \t are permitted)
        if (text.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new ArgumentException("The file contains invalid control characters.");

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
    //  Strict Encoding Detection                                          //
    // ------------------------------------------------------------------ //

    internal static string DetectAndDecode(byte[] bytes, string? sourceLanguageHint = null)
    {
        // 1. BOM-based detection with STRICT decoder (throw on invalid bytes)
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return strictUtf8.GetString(bytes, 3, bytes.Length - 3);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException("The file contains malformed UTF-8 byte sequences.");
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            var remaining = bytes.Length - 2;
            if (remaining % 2 != 0)
                throw new ArgumentException("Invalid UTF-16 LE stream: payload byte count must be even.");

            var strictLe = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            try
            {
                return strictLe.GetString(bytes, 2, remaining);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException("The file contains malformed UTF-16 LE byte sequences.");
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            var remaining = bytes.Length - 2;
            if (remaining % 2 != 0)
                throw new ArgumentException("Invalid UTF-16 BE stream: payload byte count must be even.");

            var strictBe = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            try
            {
                return strictBe.GetString(bytes, 2, remaining);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException("The file contains malformed UTF-16 BE byte sequences.");
            }
        }

        // 2. Try UTF-8 strict (no fallback chars)
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8, proceed to CJK candidates
        }

        // 3. Heuristic: try common CJK encodings with strict exception fallback
        var candidates = GetEncodingCandidates(sourceLanguageHint);

        foreach (var name in candidates)
        {
            try
            {
                var enc = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var decoded = enc.GetString(bytes);

                // Reject if decoded text contains unprintable control characters
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

        // 4. Reject unsupported or malformed encoding completely (no silent replacement)
        throw new ArgumentException("Unable to decode file. Unsupported encoding or malformed byte sequences.");
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
