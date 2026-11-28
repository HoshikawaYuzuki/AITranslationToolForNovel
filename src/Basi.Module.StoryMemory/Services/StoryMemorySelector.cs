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

    IReadOnlyList<SceneFactContextItem> SelectSceneFactsForBatch(
        IReadOnlyList<SceneFact> facts,
        IReadOnlyList<int> batchOrders,
        IReadOnlyList<string> batchSourceTexts,
        int maxEntries = StoryMemorySelector.MaxSceneFactsPerBatch,
        int maxChars = StoryMemorySelector.MaxStoryNotesCharsPerBatch);

    IReadOnlyList<SpeechObservationContextItem> SelectSpeechObservationsForBatch(
        IReadOnlyList<SpeechObservation> observations,
        IReadOnlyList<string> batchSourceTexts,
        IReadOnlyList<GlossaryContextItem>? matchedGlossary = null,
        int maxEntries = StoryMemorySelector.MaxSpeechObservationsPerBatch,
        int maxChars = StoryMemorySelector.MaxStoryNotesCharsPerBatch);

    IReadOnlyList<StoryConsistencyIssue> CheckConsistency(
        IReadOnlyList<StoryEntity> entities,
        IEnumerable<(int Order, string SourceText, string? TranslatedText)> completedSegments);
}

/// <summary>
/// Selects bounded, batch-relevant glossary entries, scene facts, and speech observations,
/// preferring Confirmed over Provisional and excluding Unknown/Conflicted items from prompt injection.
/// </summary>
public sealed class StoryMemorySelector : IStoryMemorySelector
{
    public const int MaxGlossaryEntriesPerBatch = 20;
    public const int MaxGlossaryCharsPerBatch = 1500;
    public const int MaxSceneFactsPerBatch = 6;
    public const int MaxSpeechObservationsPerBatch = 8;
    public const int MaxStoryNotesCharsPerBatch = 600;
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

        var eligible = entities
            .Where(e => IsEligibleForContext(e.Status, e.Origin, e.EvidenceSegmentId))
            .OrderBy(e => e.Status == FactStatus.Confirmed ? 0 : 1)
            .ThenByDescending(e => e.NormalizedSourceTerm.Length)
            .ThenBy(e => e.EvidenceSegmentOrder ?? int.MaxValue)
            .ThenBy(e => e.SourceTerm, StringComparer.Ordinal)
            .ToList();

        var batchCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", batchSourceTexts ?? Array.Empty<string>()));
        var contextCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", contextSourceTexts ?? Array.Empty<string>()));

        var batchHits = new List<StoryEntity>();
        var contextHits = new List<StoryEntity>();
        var characterFillers = new List<StoryEntity>();

        foreach (var entity in eligible)
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
            characterFillers.OrderBy(e => e.EvidenceSegmentOrder ?? int.MaxValue).ThenBy(e => e.CreatedAt),
            MaxBackgroundCharacters);

        return selected;
    }

    public IReadOnlyList<SceneFactContextItem> SelectSceneFactsForBatch(
        IReadOnlyList<SceneFact> facts,
        IReadOnlyList<int> batchOrders,
        IReadOnlyList<string> batchSourceTexts,
        int maxEntries = MaxSceneFactsPerBatch,
        int maxChars = MaxStoryNotesCharsPerBatch)
    {
        if (facts == null || facts.Count == 0 || maxEntries <= 0 || maxChars <= 0)
        {
            return Array.Empty<SceneFactContextItem>();
        }

        var maxBatchOrder = batchOrders is { Count: > 0 } ? batchOrders.Max() : int.MaxValue;
        var batchCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", batchSourceTexts ?? Array.Empty<string>()));

        var ordered = facts
            .Where(f => IsEligibleForContext(f.Status, f.Origin, f.EvidenceSegmentId))
            .Where(f => !f.EvidenceSegmentOrder.HasValue || f.EvidenceSegmentOrder.Value <= maxBatchOrder)
            .OrderBy(f => f.Status == FactStatus.Confirmed ? 0 : 1)
            .ThenByDescending(f => MatchesNormalizedTerm(batchCorpus, f.NormalizedFactKey) ? 1 : 0)
            .ThenByDescending(f => f.EvidenceSegmentOrder ?? -1)
            .ThenByDescending(f => f.UpdatedAt);

        var selected = new List<SceneFactContextItem>();
        var usedChars = 0;
        foreach (var fact in ordered)
        {
            if (selected.Count >= maxEntries) break;

            var item = new SceneFactContextItem(
                StoryEntityNormalizer.SanitizeForPrompt(fact.FactKey),
                fact.FactType.ToString(),
                StoryEntityNormalizer.SanitizeForPrompt(fact.Summary),
                fact.Status.ToString());
            var lineLen = EstimateSceneFactLineLength(item);
            if (usedChars > 0 && usedChars + lineLen > maxChars) continue;
            if (usedChars == 0 && lineLen > maxChars) continue;

            selected.Add(item);
            usedChars += lineLen;
        }

        return selected;
    }

    public IReadOnlyList<SpeechObservationContextItem> SelectSpeechObservationsForBatch(
        IReadOnlyList<SpeechObservation> observations,
        IReadOnlyList<string> batchSourceTexts,
        IReadOnlyList<GlossaryContextItem>? matchedGlossary = null,
        int maxEntries = MaxSpeechObservationsPerBatch,
        int maxChars = MaxStoryNotesCharsPerBatch)
    {
        if (observations == null || observations.Count == 0 || maxEntries <= 0 || maxChars <= 0)
        {
            return Array.Empty<SpeechObservationContextItem>();
        }

        var batchCorpus = StoryEntityNormalizer.NormalizeKey(string.Join(" ", batchSourceTexts ?? Array.Empty<string>()));
        var glossarySpeakers = new HashSet<string>(StringComparer.Ordinal);
        if (matchedGlossary != null)
        {
            foreach (var g in matchedGlossary)
            {
                glossarySpeakers.Add(StoryEntityNormalizer.NormalizeKey(g.SourceTerm));
                foreach (var a in g.Aliases)
                {
                    glossarySpeakers.Add(StoryEntityNormalizer.NormalizeKey(a));
                }
            }
        }

        var prioritized = observations
            .Where(o => IsEligibleForContext(o.Status, o.Origin, o.EvidenceSegmentId))
            .Select(o => new
            {
                Obs = o,
                DirectHit = MatchesNormalizedTerm(batchCorpus, o.NormalizedSpeakerName)
                    || (!string.IsNullOrEmpty(o.NormalizedAddresseeName) && MatchesNormalizedTerm(batchCorpus, o.NormalizedAddresseeName)),
                GlossaryHit = glossarySpeakers.Contains(o.NormalizedSpeakerName)
            })
            .Where(x => x.DirectHit || x.GlossaryHit)
            .OrderByDescending(x => x.DirectHit ? 1 : 0)
            .ThenBy(x => x.Obs.Status == FactStatus.Confirmed ? 0 : 1)
            .ThenByDescending(x => x.Obs.UpdatedAt);

        var selected = new List<SpeechObservationContextItem>();
        var usedChars = 0;
        foreach (var entry in prioritized)
        {
            if (selected.Count >= maxEntries) break;

            var o = entry.Obs;
            var item = new SpeechObservationContextItem(
                StoryEntityNormalizer.SanitizeForPrompt(o.SpeakerName),
                string.IsNullOrWhiteSpace(o.AddresseeName) ? null : StoryEntityNormalizer.SanitizeForPrompt(o.AddresseeName),
                StoryEntityNormalizer.SanitizeForPrompt(o.SpeechRegister),
                string.IsNullOrWhiteSpace(o.PronounStyle) ? null : StoryEntityNormalizer.SanitizeForPrompt(o.PronounStyle),
                StoryEntityNormalizer.SanitizeForPrompt(o.Observation),
                o.Status.ToString());
            var lineLen = EstimateSpeechObservationLineLength(item);
            if (usedChars > 0 && usedChars + lineLen > maxChars) continue;
            if (usedChars == 0 && lineLen > maxChars) continue;

            selected.Add(item);
            usedChars += lineLen;
        }

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

        var activeEntities = entities
            .Where(e => IsEligibleForContext(e.Status, e.Origin, e.EvidenceSegmentId))
            .ToList();

        foreach (var seg in completedSegments)
        {
            if (string.IsNullOrWhiteSpace(seg.SourceText) || string.IsNullOrWhiteSpace(seg.TranslatedText))
            {
                continue;
            }

            var sourceCorpus = StoryEntityNormalizer.NormalizeKey(seg.SourceText);
            var targetCorpus = StoryEntityNormalizer.NormalizeKey(seg.TranslatedText);

            foreach (var entity in activeEntities)
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

    public static int EstimateSceneFactLineLength(SceneFactContextItem item) =>
        16 + item.FactType.Length + item.FactKey.Length + item.Summary.Length + item.Status.Length;

    public static int EstimateSpeechObservationLineLength(SpeechObservationContextItem item) =>
        20 + item.SpeakerName.Length + (item.AddresseeName?.Length ?? 0) + item.SpeechRegister.Length + (item.PronounStyle?.Length ?? 0) + item.Observation.Length + item.Status.Length;

    private static bool IsEligibleForContext(FactStatus status, StoryEntityOrigin origin, Guid? evidenceSegmentId) =>
        status is FactStatus.Confirmed or FactStatus.Provisional
        && (origin == StoryEntityOrigin.Manual || evidenceSegmentId.HasValue);

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

    public static bool MatchesNormalizedTerm(string normalizedCorpus, string normalizedTerm)
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
