using System.Text.RegularExpressions;
using Basi.Core.Models;

namespace Basi.Module.StoryMemory.Services;

public sealed record StoryConsistencyIssue(
    int SegmentOrder,
    Guid EntityId,
    string SourceTerm,
    string ExpectedTranslation,
    string Category,
    IReadOnlyList<string> MatchedSourceTerms);

public interface IStoryMemorySelector
{
    IReadOnlyList<GlossaryContextItem> SelectForBatch(
        IReadOnlyList<StoryEntity> entities,
        IReadOnlyList<string> batchSourceTexts,
        IReadOnlyList<string>? contextSourceTexts = null,
        int maxEntries = StoryMemorySelector.MaxGlossaryEntriesPerBatch,
        int maxChars = StoryMemorySelector.MaxGlossaryCharsPerBatch);

    IReadOnlyList<StoryConsistencyIssue> CheckConsistency(
        IReadOnlyList<StoryEntity> entities,
        IEnumerable<(int Order, string SourceText, string? TranslatedText)> completedSegments);
}

/// <summary>
/// Selects bounded, batch-relevant glossary entries for translation prompts and checks translated segment consistency.
/// </summary>
public sealed class StoryMemorySelector : IStoryMemorySelector
{
    public const int MaxGlossaryEntriesPerBatch = 20;
    public const int MaxGlossaryCharsPerBatch = 1500;
    private const int MaxBackgroundCharacters = 5;

    public IReadOnlyList<GlossaryContextItem> SelectForBatch(
        IReadOnlyList<StoryEntity> entities,
        IReadOnlyList<string> batchSourceTexts,
        IReadOnlyList<string>? contextSourceTexts = null,
        int maxEntries = MaxGlossaryEntriesPerBatch,
        int maxChars = MaxGlossaryCharsPerBatch)
    {
        if (entities == null || entities.Count == 0 || maxEntries <= 0 || maxChars <= 0)
        {
            return Array.Empty<GlossaryContextItem>();
        }

        var batchCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", batchSourceTexts ?? Array.Empty<string>()));
        var contextCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", contextSourceTexts ?? Array.Empty<string>()));

        var batchHits = new List<StoryEntity>();
        var contextHits = new List<StoryEntity>();
        var characterFillers = new List<StoryEntity>();

        foreach (var entity in entities
            .OrderByDescending(e => e.NormalizedSourceTerm.Length)
            .ThenBy(e => e.FirstSeenOrder ?? int.MaxValue)
            .ThenBy(e => e.SourceTerm, StringComparer.Ordinal))
        {
            if (MatchesEntity(batchCorpus, entity))
            {
                batchHits.Add(entity);
            }
            else if (!string.IsNullOrEmpty(contextCorpus) && MatchesEntity(contextCorpus, entity))
            {
                contextHits.Add(entity);
            }
            else if (entity.Category == StoryEntityCategory.Character)
            {
                characterFillers.Add(entity);
            }
        }

        var selected = new List<GlossaryContextItem>();
        var usedChars = 0;

        void TryAppend(IEnumerable<StoryEntity> candidates, int limit = int.MaxValue)
        {
            var added = 0;
            foreach (var entity in candidates)
            {
                if (selected.Count >= maxEntries || added >= limit) break;

                var item = ToContextItem(entity);
                var lineLen = EstimatePromptLineLength(item);
                if (usedChars > 0 && usedChars + lineLen > maxChars) continue;
                if (usedChars == 0 && lineLen > maxChars) continue;

                selected.Add(item);
                usedChars += lineLen;
                added++;
            }
        }

        TryAppend(batchHits);
        TryAppend(contextHits);
        TryAppend(
            characterFillers.OrderBy(e => e.FirstSeenOrder ?? int.MaxValue).ThenBy(e => e.CreatedAt),
            MaxBackgroundCharacters);

        return selected;
    }

    public IReadOnlyList<StoryConsistencyIssue> CheckConsistency(
        IReadOnlyList<StoryEntity> entities,
        IEnumerable<(int Order, string SourceText, string? TranslatedText)> completedSegments)
    {
        var issues = new List<StoryConsistencyIssue>();
        if (entities == null || entities.Count == 0 || completedSegments == null)
        {
            return issues;
        }

        foreach (var seg in completedSegments)
        {
            if (string.IsNullOrWhiteSpace(seg.SourceText) || string.IsNullOrWhiteSpace(seg.TranslatedText))
            {
                continue;
            }

            var sourceCorpus = StoryEntityNormalizer.NormalizeKey(seg.SourceText);
            var targetCorpus = StoryEntityNormalizer.NormalizeKey(seg.TranslatedText);

            foreach (var entity in entities)
            {
                var matchedInSource = CollectMatchedTerms(sourceCorpus, entity);
                if (matchedInSource.Count == 0) continue;

                var expectedNormalized = StoryEntityNormalizer.NormalizeKey(entity.TranslatedTerm);
                if (!MatchesNormalizedTerm(targetCorpus, expectedNormalized))
                {
                    issues.Add(new StoryConsistencyIssue(
                        seg.Order,
                        entity.Id,
                        entity.SourceTerm,
                        entity.TranslatedTerm,
                        entity.Category.ToString(),
                        matchedInSource));
                }
            }
        }

        return issues;
    }

    public static int EstimatePromptLineLength(GlossaryContextItem item)
    {
        // Format: "- {SourceTerm} -> {TranslatedTerm} [{Category}] (Aliases: a, b) | Note: {Description}\n"
        var len = 10 + item.SourceTerm.Length + item.TranslatedTerm.Length + item.Category.Length;
        if (item.Aliases.Count > 0)
        {
            len += 12 + item.Aliases.Sum(a => a.Length + 2);
        }
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            len += 9 + item.Description.Length;
        }
        return len;
    }

    private static GlossaryContextItem ToContextItem(StoryEntity entity)
    {
        return new GlossaryContextItem(
            StoryEntityNormalizer.SanitizeForPrompt(entity.SourceTerm),
            StoryEntityNormalizer.SanitizeForPrompt(entity.TranslatedTerm),
            entity.Category.ToString(),
            entity.Aliases.Select(StoryEntityNormalizer.SanitizeForPrompt).Where(a => !string.IsNullOrEmpty(a)).ToList(),
            string.IsNullOrWhiteSpace(entity.Description) ? null : StoryEntityNormalizer.SanitizeForPrompt(entity.Description));
    }

    private static bool MatchesEntity(string normalizedCorpus, StoryEntity entity)
    {
        if (string.IsNullOrEmpty(normalizedCorpus)) return false;

        if (MatchesNormalizedTerm(normalizedCorpus, entity.NormalizedSourceTerm))
        {
            return true;
        }

        foreach (var alias in entity.Aliases)
        {
            if (MatchesNormalizedTerm(normalizedCorpus, StoryEntityNormalizer.NormalizeKey(alias)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> CollectMatchedTerms(string normalizedCorpus, StoryEntity entity)
    {
        var matched = new List<string>();
        if (MatchesNormalizedTerm(normalizedCorpus, entity.NormalizedSourceTerm))
        {
            matched.Add(entity.SourceTerm);
        }

        foreach (var alias in entity.Aliases)
        {
            if (MatchesNormalizedTerm(normalizedCorpus, StoryEntityNormalizer.NormalizeKey(alias)))
            {
                matched.Add(alias);
            }
        }

        return matched;
    }

    internal static bool MatchesNormalizedTerm(string normalizedCorpus, string normalizedTerm)
    {
        if (string.IsNullOrEmpty(normalizedCorpus) || string.IsNullOrEmpty(normalizedTerm))
        {
            return false;
        }

        if (IsPureAsciiWordTerm(normalizedTerm))
        {
            var pattern = $@"(?<![a-z0-9]){Regex.Escape(normalizedTerm)}(?![a-z0-9])";
            return Regex.IsMatch(normalizedCorpus, pattern, RegexOptions.CultureInvariant);
        }

        return normalizedCorpus.Contains(normalizedTerm, StringComparison.Ordinal);
    }

    private static bool IsPureAsciiWordTerm(string term)
    {
        if (term.Length == 0) return false;
        var first = term[0];
        var last = term[^1];
        if (!char.IsAsciiLetterOrDigit(first) || !char.IsAsciiLetterOrDigit(last))
        {
            return false;
        }

        foreach (var ch in term)
        {
            if (ch > '\u007F') return false;
        }

        return true;
    }
}
