using System.Text;
using System.Text.RegularExpressions;
using Basi.Core.Models;

namespace Basi.Module.StoryMemory.Services;

/// <summary>
/// Canonicalizes story memory terms, aliases, categories, and prompt-injected text.
/// </summary>
public static partial class StoryEntityNormalizer
{
    public const int MaxTermLength = 256;
    public const int MaxDescriptionLength = 1000;
    public const int MaxAliasLength = 128;
    public const int MaxAliasCount = 20;

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return CollapseWhitespace(value)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
    }

    public static string NormalizeDisplayTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var collapsed = CollapseWhitespace(value);
        return collapsed.Length > MaxTermLength ? collapsed[..MaxTermLength] : collapsed;
    }

    public static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var collapsed = CollapseWhitespace(value);
        return collapsed.Length > MaxDescriptionLength ? collapsed[..MaxDescriptionLength] : collapsed;
    }

    public static List<string> NormalizeAliases(string canonicalSourceTerm, IEnumerable<string>? aliases)
    {
        var result = new List<string>();
        if (aliases == null) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonicalKey = NormalizeKey(canonicalSourceTerm);
        if (!string.IsNullOrEmpty(canonicalKey))
        {
            seen.Add(canonicalKey);
        }

        foreach (var raw in aliases)
        {
            var clean = CollapseWhitespace(raw);
            if (string.IsNullOrEmpty(clean)) continue;
            if (clean.Length > MaxAliasLength) clean = clean[..MaxAliasLength];

            var key = NormalizeKey(clean);
            if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

            result.Add(clean);
            if (result.Count >= MaxAliasCount) break;
        }

        return result;
    }

    public static bool TryParseCategory(string? raw, out StoryEntityCategory category)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            category = StoryEntityCategory.Other;
            return true;
        }

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out category)
            && Enum.IsDefined(category);
    }

    public static StoryEntityCategory ParseCategoryLenient(string? raw)
    {
        return TryParseCategory(raw, out var parsed) ? parsed : StoryEntityCategory.Other;
    }

    /// <summary>
    /// Strips [SEG: markers and line breaks before injecting glossary entries into prompts.
    /// </summary>
    public static string SanitizeForPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var flattened = CollapseWhitespace(value);
        return SegmentMarkerRegex().Replace(flattened, "[SEG_");
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return MultiWhitespaceRegex().Replace(value.Trim(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex(@"\[SEG:", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentMarkerRegex();
}
