using System.Text;
using System.Text.Json;
using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.StoryMemory.Services;

public sealed record ExtractedEntityCandidate(
    string SourceTerm,
    string TranslatedTerm,
    StoryEntityCategory Category,
    List<string> Aliases,
    string? Description,
    int? FirstSeenOrder);

public sealed record StoryMemoryExtractionResult(
    IReadOnlyList<ExtractedEntityCandidate> Candidates,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string ModelUsed);

public sealed class StoryMemoryExtractionException : Exception
{
    public int PromptTokens { get; }
    public int CompletionTokens { get; }
    public int TotalTokens { get; }
    public string ModelUsed { get; }

    public StoryMemoryExtractionException(
        string message,
        int promptTokens,
        int completionTokens,
        int totalTokens,
        string modelUsed)
        : base(message)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        ModelUsed = modelUsed;
    }
}

public interface IStoryMemoryExtractor
{
    Task<StoryMemoryExtractionResult> ExtractAsync(
        IAiProvider provider,
        string model,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<(int Order, string Content)> segments,
        IReadOnlyList<StoryEntity>? existingEntities = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds structured entity/glossary extraction prompts, calls IAiProvider, and validates strict JSON responses.
/// </summary>
public sealed class StoryMemoryExtractor : IStoryMemoryExtractor
{
    public const int MaxExtractionSegments = 20;
    public const int MaxExtractionChars = 12_000;
    private const int MaxExistingHints = 50;

    public async Task<StoryMemoryExtractionResult> ExtractAsync(
        IAiProvider provider,
        string model,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<(int Order, string Content)> segments,
        IReadOnlyList<StoryEntity>? existingEntities = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
        {
            return new StoryMemoryExtractionResult(Array.Empty<ExtractedEntityCandidate>(), 0, 0, 0, model);
        }

        if (segments.Count > MaxExtractionSegments)
        {
            throw new ArgumentException($"Extraction window cannot exceed {MaxExtractionSegments} segments per request.", nameof(segments));
        }

        var totalChars = segments.Sum(s => s.Content?.Length ?? 0);
        if (totalChars > MaxExtractionChars)
        {
            throw new ArgumentException($"Extraction input exceeds maximum character limit ({MaxExtractionChars} characters).", nameof(segments));
        }

        var (systemPrompt, userPrompt) = BuildPrompt(sourceLanguage, targetLanguage, segments, existingEntities);
        var request = new AiRequest(
            Model: model,
            SystemPrompt: systemPrompt,
            UserPrompt: userPrompt,
            MaxTokens: 4096,
            Temperature: 0.2);

        AiResponse response;
        try
        {
            response = await provider.CompleteAsync(request, ct);
        }
        catch (AiProviderError ex) when (ex.ErrorCode != AiErrorCode.InvalidApiKey)
        {
            throw new StoryMemoryExtractionException(
                ex.Message,
                ex.PromptTokens,
                ex.CompletionTokens,
                ex.TotalTokens,
                ex.ModelUsed ?? model);
        }

        if (response.FinishReason == FinishReason.MaxTokens)
        {
            throw new StoryMemoryExtractionException(
                "Story memory extraction output was truncated because MaxTokens limit was reached.",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

        if (response.FinishReason == FinishReason.ContentFiltered)
        {
            throw new StoryMemoryExtractionException(
                "Story memory extraction response was blocked by content filter.",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

        if (response.FinishReason != FinishReason.Stop)
        {
            throw new StoryMemoryExtractionException(
                $"Story memory extraction did not complete successfully (FinishReason: {response.FinishReason}).",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

        IReadOnlyList<ExtractedEntityCandidate> candidates;
        try
        {
            candidates = ParseResponse(response.Content);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new StoryMemoryExtractionException(
                $"Model returned invalid story memory JSON: {ex.Message}",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

        return new StoryMemoryExtractionResult(
            candidates,
            response.PromptTokens,
            response.CompletionTokens,
            response.TotalTokens,
            response.ModelUsed);
    }

    internal static (string SystemPrompt, string UserPrompt) BuildPrompt(
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<(int Order, string Content)> segments,
        IReadOnlyList<StoryEntity>? existingEntities = null)
    {
        var systemPrompt =
            $"You are an expert literary story-bible and terminology analyst for {sourceLanguage} -> {targetLanguage} novel translation.\n" +
            $"Extract recurring proper nouns, character names, locations, skills/abilities, items, organizations, concepts, and honorific terms.\n" +
            $"Return STRICT JSON ONLY with no surrounding prose, using this exact schema:\n" +
            $"{{\"entities\":[{{\"sourceTerm\":\"string\",\"translatedTerm\":\"string\",\"category\":\"Character|Location|Skill|Item|Organization|Concept|Honorific|Other\",\"aliases\":[\"string\"],\"description\":\"string\",\"firstSeenOrder\":0}}]}}";

        var sb = new StringBuilder();
        sb.AppendLine("--- STORY MEMORY EXTRACTION ---");
        sb.AppendLine($"SourceLanguage: {sourceLanguage.Trim()}");
        sb.AppendLine($"TargetLanguage: {targetLanguage.Trim()}");
        sb.AppendLine();

        if (existingEntities != null && existingEntities.Count > 0)
        {
            sb.AppendLine("--- EXISTING GLOSSARY TERMS (KEEP CONSISTENT; ADD ONLY NEW TERMS OR MISSING ALIASES) ---");
            foreach (var existing in existingEntities.Take(MaxExistingHints))
            {
                sb.AppendLine($"- {StoryEntityNormalizer.SanitizeForPrompt(existing.SourceTerm)} -> {StoryEntityNormalizer.SanitizeForPrompt(existing.TranslatedTerm)} [{existing.Category}]");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- NOVEL SEGMENTS ---");
        foreach (var (order, content) in segments)
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine($"[SEG:{order}] {content.Trim()}");
            }
        }

        return (systemPrompt, sb.ToString().TrimEnd());
    }

    internal static IReadOnlyList<ExtractedEntityCandidate> ParseResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Extraction response was empty.");
        }

        var jsonText = ExtractJsonPayload(content);
        using var doc = JsonDocument.Parse(jsonText);

        JsonElement entitiesArray;
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (!doc.RootElement.TryGetProperty("entities", out entitiesArray) || entitiesArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("JSON object must contain an 'entities' array.");
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            entitiesArray = doc.RootElement;
        }
        else
        {
            throw new InvalidOperationException("Root JSON value must be an object with 'entities' or an array.");
        }

        var byKey = new Dictionary<string, ExtractedEntityCandidate>(StringComparer.Ordinal);
        foreach (var el in entitiesArray.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var sourceRaw = ReadOptionalString(el, "sourceTerm");
            var translatedRaw = ReadOptionalString(el, "translatedTerm");
            var sourceTerm = StoryEntityNormalizer.NormalizeDisplayTerm(sourceRaw);
            var translatedTerm = StoryEntityNormalizer.NormalizeDisplayTerm(translatedRaw);
            if (string.IsNullOrWhiteSpace(sourceTerm) || string.IsNullOrWhiteSpace(translatedTerm))
            {
                continue;
            }

            var key = StoryEntityNormalizer.NormalizeKey(sourceTerm);
            var category = StoryEntityNormalizer.ParseCategoryLenient(ReadOptionalString(el, "category"));
            var description = StoryEntityNormalizer.NormalizeDescription(ReadOptionalString(el, "description"));
            var rawAliases = ReadStringArray(el, "aliases");
            var aliases = StoryEntityNormalizer.NormalizeAliases(sourceTerm, rawAliases);
            int? firstSeenOrder = el.TryGetProperty("firstSeenOrder", out var ordEl) && ordEl.ValueKind == JsonValueKind.Number && ordEl.TryGetInt32(out var ordVal) && ordVal >= 0
                ? ordVal
                : null;

            if (byKey.TryGetValue(key, out var existing))
            {
                var mergedAliases = StoryEntityNormalizer.NormalizeAliases(existing.SourceTerm, existing.Aliases.Concat(aliases));
                byKey[key] = existing with
                {
                    Aliases = mergedAliases,
                    Description = existing.Description ?? description,
                    FirstSeenOrder = existing.FirstSeenOrder ?? firstSeenOrder
                };
            }
            else
            {
                byKey[key] = new ExtractedEntityCandidate(
                    sourceTerm,
                    translatedTerm,
                    category,
                    aliases,
                    description,
                    firstSeenOrder);
            }
        }

        return byKey.Values.ToList();
    }

    private static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var objStart = trimmed.IndexOf('{');
        var arrStart = trimmed.IndexOf('[');
        if (objStart >= 0 && (arrStart < 0 || objStart < arrStart))
        {
            var objEnd = trimmed.LastIndexOf('}');
            if (objEnd > objStart) return trimmed[objStart..(objEnd + 1)];
        }
        else if (arrStart >= 0)
        {
            var arrEnd = trimmed.LastIndexOf(']');
            if (arrEnd > arrStart) return trimmed[arrStart..(arrEnd + 1)];
        }

        throw new InvalidOperationException("No JSON object or array found in extraction response.");
    }

    private static string? ReadOptionalString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }
        return list;
    }
}
