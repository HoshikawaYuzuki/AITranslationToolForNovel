using System.Text;
using System.Text.RegularExpressions;
using Basi.Core.Models;

namespace Basi.Module.StoryMemory.Services;

/// <summary>
/// Canonicalizes story memory terms, aliases, quotes, enums, and prompt-injected text with strict Unicode bounds.
/// </summary>
public static partial class StoryEntityNormalizer
{
    public const int MaxTermLength = 256;
    public const int MaxAliasLength = 128;
    public const int MaxAliasCount = 20;
    public const int MaxDescriptionLength = 1000;
    public const int MaxEvidenceQuoteLength = 500;
    public const int MaxSpeechRegisterLength = 128;

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return CollapseWhitespace(value)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
    }

    public static bool ContainsInvalidUtf16Surrogates(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }
                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryValidateTerm(
        string? raw,
        string fieldName,
        out string displayTerm,
        out string normalizedKey,
        out string? error,
        int maxLength = MaxTermLength)
    {
        displayTerm = string.Empty;
        normalizedKey = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{fieldName} is required.";
            return false;
        }

        if (ContainsInvalidUtf16Surrogates(raw))
        {
            error = $"{fieldName} contains unpaired Unicode surrogates.";
            return false;
        }

        var collapsed = CollapseWhitespace(raw);
        var norm = collapsed.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(norm))
        {
            error = $"{fieldName} cannot be empty after normalization.";
            return false;
        }

        if (collapsed.Length > maxLength || norm.Length > maxLength)
        {
            error = $"{fieldName} exceeds maximum allowed length of {maxLength} characters after Unicode normalization (normalized length: {norm.Length}).";
            return false;
        }

        displayTerm = collapsed;
        normalizedKey = norm;
        return true;
    }

    public static bool TryValidateOptionalText(
        string? raw,
        string fieldName,
        int maxLength,
        out string? normalizedText,
        out string? error)
    {
        normalizedText = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (ContainsInvalidUtf16Surrogates(raw))
        {
            error = $"{fieldName} contains unpaired Unicode surrogates.";
            return false;
        }

        var collapsed = CollapseWhitespace(raw);
        if (collapsed.Length > maxLength)
        {
            error = $"{fieldName} must not exceed {maxLength} characters.";
            return false;
        }

        normalizedText = collapsed;
        return true;
    }

    public static bool TryValidateAliases(
        string canonicalSourceTerm,
        IEnumerable<string>? aliases,
        out List<string> normalizedAliases,
        out string? error)
    {
        normalizedAliases = new List<string>();
        error = null;
        if (aliases == null) return true;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var canonicalKey = NormalizeKey(canonicalSourceTerm);
        if (!string.IsNullOrEmpty(canonicalKey))
        {
            seen.Add(canonicalKey);
        }

        foreach (var raw in aliases)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (!TryValidateTerm(raw, "Alias", out var cleanAlias, out var key, out var aliasError, MaxAliasLength))
            {
                error = aliasError;
                return false;
            }

            if (!seen.Add(key)) continue;

            normalizedAliases.Add(cleanAlias);
            if (normalizedAliases.Count > MaxAliasCount)
            {
                error = $"Aliases list cannot exceed {MaxAliasCount} distinct entries.";
                return false;
            }
        }

        return true;
    }

    public static bool MatchesNormalizedQuote(string segmentContent, string quote)
    {
        var normSeg = NormalizeKey(segmentContent);
        var normQuote = NormalizeKey(quote);
        if (string.IsNullOrEmpty(normSeg) || string.IsNullOrEmpty(normQuote))
        {
            return false;
        }
        return normSeg.Contains(normQuote, StringComparison.Ordinal);
    }

    public static bool TryParseCategory(string? raw, out StoryEntityCategory category)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            category = StoryEntityCategory.Other;
            return true;
        }

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, out _))
        {
            category = default;
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out category)
            && Enum.IsDefined(category);
    }

    public static bool TryParseFactStatus(string? raw, out FactStatus status)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            status = FactStatus.Provisional;
            return true;
        }

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, out _))
        {
            status = default;
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out status)
            && Enum.IsDefined(status);
    }

    public static bool TryParseSceneFactType(string? raw, out SceneFactType factType)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            factType = SceneFactType.Other;
            return true;
        }

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, out _))
        {
            factType = default;
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out factType)
            && Enum.IsDefined(factType);
    }

    /// <summary>
    /// Strips [SEG: markers and line breaks before injecting story memory entries into prompts.
    /// </summary>
    public static string SanitizeForPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var flattened = CollapseWhitespace(value);
        return SegmentMarkerRegex().Replace(flattened, "[SEG_");
    }

    /// <summary>
    /// Truncates text safely without splitting UTF-16 surrogate pairs.
    /// </summary>
    public static string TruncateSurrogateSafe(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;
        if (value.Length <= maxLength) return value;
        if (maxLength <= 3) return value[..AdjustForSurrogateBoundary(value, maxLength)];

        var cut = AdjustForSurrogateBoundary(value, maxLength - 3);
        return string.Concat(value.AsSpan(0, cut), "...");
    }

    /// <summary>
    /// Builds a bounded, surrogate-safe conflict message guaranteed to fit MaxDescriptionLength (1000 chars).
    /// </summary>
    public static string BuildConflictDetails(
        string incomingLabel,
        string incomingValue,
        string existingValue,
        int? segmentOrder)
    {
        const int maxPerValueChars = 350;
        var cleanIncoming = TruncateSurrogateSafe(CollapseWhitespace(incomingValue), maxPerValueChars);
        var cleanExisting = TruncateSurrogateSafe(CollapseWhitespace(existingValue), maxPerValueChars);
        var segSuffix = segmentOrder.HasValue ? $" (segment #{segmentOrder.Value})" : string.Empty;
        var formatted = $"AI extraction {incomingLabel} '{cleanIncoming}'{segSuffix} conflicted with '{cleanExisting}'.";
        return TruncateSurrogateSafe(formatted, MaxDescriptionLength);
    }

    private static int AdjustForSurrogateBoundary(string value, int candidateLength)
    {
        if (candidateLength <= 0) return 0;
        if (candidateLength >= value.Length) return value.Length;
        return char.IsHighSurrogate(value[candidateLength - 1]) ? candidateLength - 1 : candidateLength;
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
