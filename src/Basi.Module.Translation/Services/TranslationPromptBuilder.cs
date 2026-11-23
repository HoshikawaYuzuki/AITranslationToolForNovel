using System.Text;
using System.Text.RegularExpressions;

namespace Basi.Module.Translation.Services;

public record SegmentItem(int Order, string SourceText);
public record ContextSegmentItem(int Order, string SourceText, string TranslatedText);
public record TranslationPrompt(string SystemPrompt, string UserPrompt);

public interface ITranslationPromptBuilder
{
    TranslationPrompt BuildPrompt(
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentItem> segmentsToTranslate,
        IReadOnlyList<ContextSegmentItem>? contextHistory = null);

    IReadOnlyDictionary<int, string> ParseResponse(string content, IEnumerable<int> expectedOrders);
}

public sealed partial class TranslationPromptBuilder : ITranslationPromptBuilder
{
    public TranslationPrompt BuildPrompt(
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentItem> segmentsToTranslate,
        IReadOnlyList<ContextSegmentItem>? contextHistory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(segmentsToTranslate);

        var systemPrompt =
            $"You are an expert literary and novel translator specializing in fiction and creative writing. " +
            $"Translate the following novel text from {sourceLanguage} into {targetLanguage}.\n\n" +
            $"Translation Guidelines:\n" +
            $"- Provide natural, fluent, and evocative phrasing suitable for literature and web novels.\n" +
            $"- Preserve each character's unique voice, honorific nuances, and distinct speech mannerisms.\n" +
            $"- Maintain consistent terminology and character relationships throughout.\n" +
            $"- Retain exact segment boundaries without merging or omitting segments.\n" +
            $"- Output format: strictly output each translated segment starting with its exact identifier tag, e.g. [SEG:0] Translated paragraph...\n" +
            $"- Do NOT include any conversational filler, notes, greetings, or explanations outside the [SEG:X] blocks.";

        var sb = new StringBuilder();

        // Include preceding context window if available to maintain narrative continuity
        if (contextHistory != null && contextHistory.Count > 0)
        {
            sb.AppendLine("--- RECENT CONTEXT (FOR CONTINUITY ONLY — DO NOT TRANSLATE AGAIN) ---");
            foreach (var ctx in contextHistory)
            {
                if (!string.IsNullOrWhiteSpace(ctx.TranslatedText))
                {
                    sb.AppendLine($"[SEG:{ctx.Order}] {ctx.TranslatedText.Trim()}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- SEGMENTS TO TRANSLATE ---");
        foreach (var seg in segmentsToTranslate)
        {
            sb.AppendLine($"[SEG:{seg.Order}] {seg.SourceText.Trim()}");
        }

        return new TranslationPrompt(systemPrompt, sb.ToString().TrimEnd());
    }

    public IReadOnlyDictionary<int, string> ParseResponse(string content, IEnumerable<int> expectedOrders)
    {
        ArgumentNullException.ThrowIfNull(expectedOrders);

        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var expectedSet = expectedOrders.ToHashSet();
        var seenOrders = new HashSet<int>();
        var duplicateOrders = new List<int>();
        var unexpectedOrders = new List<int>();

        // N01 / M01: Unified strict tag validation pass for all [SEG:...] occurrences
        // Grammar strictly requires: [SEG:\d+] with non-negative integer, no whitespace, no sign
        var allTagsPattern = AllSegmentTagsRegex();
        foreach (Match tagMatch in allTagsPattern.Matches(content))
        {
            var rawTag = tagMatch.Groups[1].Value;
            if (!DigitsOnlyRegex().IsMatch(rawTag) || !int.TryParse(rawTag, out var order) || order < 0)
            {
                throw new InvalidOperationException(
                    $"Model response contained malformed segment tag: '{tagMatch.Value}'");
            }

            if (!seenOrders.Add(order))
            {
                duplicateOrders.Add(order);
            }
            else if (!expectedSet.Contains(order))
            {
                unexpectedOrders.Add(order);
            }
        }

        if (duplicateOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Model response contained duplicate segment tags: {string.Join(", ", duplicateOrders.Distinct())}");
        }

        if (unexpectedOrders.Count > 0)
        {
            throw new InvalidOperationException(
                $"Model response contained unexpected segment tags: {string.Join(", ", unexpectedOrders.Distinct())}");
        }

        var pattern = SegmentTagRegex();
        var matches = pattern.Matches(content);

        foreach (Match match in matches)
        {
            var order = int.Parse(match.Groups[1].Value);
            var text = match.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result[order] = text;
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsOnlyRegex();

    [GeneratedRegex(@"\[SEG:([^\]]*)\]")]
    private static partial Regex AllSegmentTagsRegex();

    [GeneratedRegex(@"\[SEG:(\d+)\]\s*([\s\S]*?)(?=(?:\[SEG:\d+\])|\z)")]
    private static partial Regex SegmentTagRegex();
}
