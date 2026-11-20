using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

/// <summary>
/// Parses a raw TXT file into a list of <see cref="Segment"/> records.
/// </summary>
public sealed class TxtParser
{
    private const int MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Parses raw bytes into an ordered list of segments.
    /// </summary>
    /// <param name="bytes">Raw file bytes.</param>
    /// <param name="novelId">The novel these segments belong to.</param>
    /// <returns>Ordered, non-empty list of segments.</returns>
    /// <exception cref="ArgumentException">When the file is too large.</exception>
    /// <exception cref="InvalidOperationException">When no non-empty paragraphs are found.</exception>
    public IReadOnlyList<Segment> Parse(byte[] bytes, Guid novelId)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length > MaxFileSizeBytes)
            throw new ArgumentException(
                $"File size {bytes.Length} bytes exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB limit.");

        var text = DetectAndDecode(bytes);
        return SplitIntoSegments(text, novelId);
    }

    // ------------------------------------------------------------------ //
    //  Encoding detection                                                  //
    // ------------------------------------------------------------------ //

    internal static string DetectAndDecode(byte[] bytes)
    {
        // 1. BOM-based detection (fastest, most reliable)
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); // UTF-16 LE
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); // UTF-16 BE
        }

        // 2. Try UTF-8 strict (no fallback chars)
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try { return utf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { /* not valid UTF-8 */ }

        // 3. Heuristic: try common CJK encodings in order
        var candidates = new[]
        {
            System.Text.Encoding.GetEncoding("shift_jis"),
            System.Text.Encoding.GetEncoding("gb18030"),
            System.Text.Encoding.GetEncoding("euc-kr"),
        };

        foreach (var enc in candidates)
        {
            try
            {
                // Re-encode round-trip sanity check
                var decoded = enc.GetString(bytes);
                var reEncoded = enc.GetBytes(decoded);
                if (reEncoded.SequenceEqual(bytes))
                    return decoded;
            }
            catch { /* try next */ }
        }

        // 4. Final fallback: UTF-8 with replacement
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // ------------------------------------------------------------------ //
    //  Segmentation                                                        //
    // ------------------------------------------------------------------ //

    internal static IReadOnlyList<Segment> SplitIntoSegments(string text, Guid novelId)
    {
        // Normalize line endings then split on blank lines (paragraph boundary)
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var paragraphs = normalized
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
