using System.Text;
using System.Text.Json;
using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.StoryMemory.Services;

public sealed record SourceSegmentInput(
    Guid Id,
    string StableId,
    int Order,
    string Content);

public sealed record ExtractedEntityCandidate(
    string SourceTerm,
    string NormalizedSourceTerm,
    string TranslatedTerm,
    StoryEntityCategory Category,
    List<string> Aliases,
    string? Description,
    Guid EvidenceSegmentId,
    string EvidenceSegmentStableId,
    int EvidenceSegmentOrder,
    string? EvidenceQuote);

public sealed record ExtractedSceneFactCandidate(
    string FactKey,
    string NormalizedFactKey,
    SceneFactType FactType,
    string Summary,
    Guid EvidenceSegmentId,
    string EvidenceSegmentStableId,
    int EvidenceSegmentOrder,
    string? EvidenceQuote);

public sealed record ExtractedSpeechObservationCandidate(
    string SpeakerName,
    string NormalizedSpeakerName,
    string? AddresseeName,
    string NormalizedAddresseeName,
    string SpeechRegister,
    string? PronounStyle,
    string Observation,
    Guid EvidenceSegmentId,
    string EvidenceSegmentStableId,
    int EvidenceSegmentOrder,
    string? EvidenceQuote);

public sealed record StoryMemoryExtractionResult(
    IReadOnlyList<ExtractedEntityCandidate> Entities,
    IReadOnlyList<ExtractedSceneFactCandidate> SceneFacts,
    IReadOnlyList<ExtractedSpeechObservationCandidate> SpeechObservations,
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
        IReadOnlyList<SourceSegmentInput> segments,
        IReadOnlyList<StoryEntity>? existingEntities = null,
        CancellationToken ct = default);
}

/// <summary>
/// Builds structured Story Memory Lite extraction prompts, calls IAiProvider,
/// strictly validates JSON schema, Unicode bounds, and source segment/quote evidence.
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
        IReadOnlyList<SourceSegmentInput> segments,
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
            return new StoryMemoryExtractionResult(
                Array.Empty<ExtractedEntityCandidate>(),
                Array.Empty<ExtractedSceneFactCandidate>(),
                Array.Empty<ExtractedSpeechObservationCandidate>(),
                0, 0, 0, model);
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

        try
        {
            var (entities, sceneFacts, speechObservations) = ParseAndValidateResponse(response.Content, segments);
            return new StoryMemoryExtractionResult(
                entities,
                sceneFacts,
                speechObservations,
                response.PromptTokens,
                response.CompletionTokens,
                response.TotalTokens,
                response.ModelUsed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new StoryMemoryExtractionException(
                $"Invalid story memory schema or ungrounded evidence: {ex.Message}",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }
    }

    internal static (string SystemPrompt, string UserPrompt) BuildPrompt(
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SourceSegmentInput> segments,
        IReadOnlyList<StoryEntity>? existingEntities = null)
    {
        var systemPrompt =
            $"You are an expert literary story-bible analyst for {sourceLanguage} -> {targetLanguage} novel translation.\n" +
            $"Extract: (1) recurring entities/glossary terms, (2) scene continuity facts, and (3) character speech/register observations.\n" +
            $"Every item MUST cite its exact evidence segment (`evidenceSegmentOrder` / `evidenceSegmentStableId` from the supplied segments) and an exact supporting `evidenceQuote` copied from that segment.\n" +
            $"Return STRICT JSON ONLY with no prose outside the JSON object:\n" +
            $"{{\"entities\":[{{\"sourceTerm\":\"string\",\"translatedTerm\":\"string\",\"category\":\"Character|Location|Skill|Item|Organization|Concept|Honorific|Other\",\"aliases\":[\"string\"],\"description\":\"string|null\",\"evidenceSegmentOrder\":0,\"evidenceSegmentStableId\":\"string\",\"evidenceQuote\":\"string\"}}]," +
            $"\"sceneFacts\":[{{\"factKey\":\"string\",\"factType\":\"Setting|Time|PresentCharacters|EventState|Other\",\"summary\":\"string\",\"evidenceSegmentOrder\":0,\"evidenceSegmentStableId\":\"string\",\"evidenceQuote\":\"string\"}}]," +
            $"\"speechObservations\":[{{\"speakerName\":\"string\",\"addresseeName\":\"string|null\",\"speechRegister\":\"string\",\"pronounStyle\":\"string|null\",\"observation\":\"string\",\"evidenceSegmentOrder\":0,\"evidenceSegmentStableId\":\"string\",\"evidenceQuote\":\"string\"}}]}}";

        var sb = new StringBuilder();
        sb.AppendLine("--- STORY MEMORY EXTRACTION ---");
        sb.AppendLine($"SourceLanguage: {sourceLanguage.Trim()}");
        sb.AppendLine($"TargetLanguage: {targetLanguage.Trim()}");
        sb.AppendLine();

        if (existingEntities != null && existingEntities.Count > 0)
        {
            sb.AppendLine("--- EXISTING GLOSSARY TERMS (KEEP CONSISTENT; ONLY REPORT GENUINELY EVIDENCED ITEMS) ---");
            foreach (var existing in existingEntities.Take(MaxExistingHints))
            {
                sb.AppendLine($"- {StoryEntityNormalizer.SanitizeForPrompt(existing.SourceTerm)} -> {StoryEntityNormalizer.SanitizeForPrompt(existing.TranslatedTerm)} [{existing.Category}]");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- NOVEL SEGMENTS ---");
        foreach (var seg in segments)
        {
            if (!string.IsNullOrWhiteSpace(seg.Content))
            {
                sb.AppendLine($"[SEG:{seg.Order} | ID:{seg.StableId}] {seg.Content.Trim()}");
            }
        }

        return (systemPrompt, sb.ToString().TrimEnd());
    }

    internal static (
        IReadOnlyList<ExtractedEntityCandidate> Entities,
        IReadOnlyList<ExtractedSceneFactCandidate> SceneFacts,
        IReadOnlyList<ExtractedSpeechObservationCandidate> SpeechObservations)
        ParseAndValidateResponse(string content, IReadOnlyList<SourceSegmentInput> suppliedSegments)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Extraction response was empty.");
        }

        var byOrder = suppliedSegments.ToDictionary(s => s.Order);
        var byStableId = suppliedSegments
            .Where(s => !string.IsNullOrWhiteSpace(s.StableId))
            .ToDictionary(s => s.StableId, StringComparer.OrdinalIgnoreCase);

        var jsonText = ExtractJsonObjectPayload(content);
        using var doc = JsonDocument.Parse(jsonText);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Root JSON value must be an object containing an 'entities' array.");
        }

        var root = doc.RootElement;
        if (!root.TryGetProperty("entities", out var entitiesEl) || entitiesEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Root JSON object must include an 'entities' array.");
        }

        var entitiesByKey = new Dictionary<string, ExtractedEntityCandidate>(StringComparer.Ordinal);
        foreach (var item in entitiesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Every element in 'entities' must be a JSON object.");
            }

            var sourceRaw = ReadRequiredString(item, "sourceTerm", "entities");
            var translatedRaw = ReadRequiredString(item, "translatedTerm", "entities");
            if (!StoryEntityNormalizer.TryValidateTerm(sourceRaw, "entities.sourceTerm", out var sourceTerm, out var normSource, out var err1))
            {
                throw new InvalidOperationException(err1);
            }
            if (!StoryEntityNormalizer.TryValidateTerm(translatedRaw, "entities.translatedTerm", out var translatedTerm, out _, out var err2))
            {
                throw new InvalidOperationException(err2);
            }

            var categoryRaw = ReadRequiredString(item, "category", "entities");
            if (!StoryEntityNormalizer.TryParseCategory(categoryRaw, out var category))
            {
                throw new InvalidOperationException($"Invalid entity category '{categoryRaw}'.");
            }

            var rawAliases = ReadStrictOptionalStringArray(item, "aliases", "entities");
            if (!StoryEntityNormalizer.TryValidateAliases(sourceTerm, rawAliases, out var aliases, out var aliasErr))
            {
                throw new InvalidOperationException(aliasErr);
            }

            var descRaw = ReadStrictOptionalString(item, "description", "entities");
            if (!StoryEntityNormalizer.TryValidateOptionalText(descRaw, "entities.description", StoryEntityNormalizer.MaxDescriptionLength, out var description, out var descErr))
            {
                throw new InvalidOperationException(descErr);
            }

            var (evidenceSeg, quote) = ResolveAndValidateEvidence(item, "entities", byOrder, byStableId);

            // Grounding check: entity sourceTerm or one of its aliases must occur in the referenced evidence segment
            var normSegContent = StoryEntityNormalizer.NormalizeKey(evidenceSeg.Content);
            var termMatched = StoryMemorySelector.MatchesNormalizedTerm(normSegContent, normSource)
                || aliases.Any(a => StoryMemorySelector.MatchesNormalizedTerm(normSegContent, StoryEntityNormalizer.NormalizeKey(a)));
            if (!termMatched)
            {
                throw new InvalidOperationException($"Entity '{sourceTerm}' was not found in referenced segment order {evidenceSeg.Order}.");
            }

            if (entitiesByKey.TryGetValue(normSource, out var existing))
            {
                if (!StoryEntityNormalizer.TryValidateAliases(existing.SourceTerm, existing.Aliases.Concat(aliases), out var mergedAliases, out var mergeAliasErr))
                {
                    throw new InvalidOperationException(mergeAliasErr);
                }
                entitiesByKey[normSource] = existing with
                {
                    Aliases = mergedAliases,
                    Description = existing.Description ?? description
                };
            }
            else
            {
                entitiesByKey[normSource] = new ExtractedEntityCandidate(
                    sourceTerm,
                    normSource,
                    translatedTerm,
                    category,
                    aliases,
                    description,
                    evidenceSeg.Id,
                    evidenceSeg.StableId,
                    evidenceSeg.Order,
                    quote);
            }
        }

        var sceneFactsByKey = new Dictionary<string, ExtractedSceneFactCandidate>(StringComparer.Ordinal);
        if (root.TryGetProperty("sceneFacts", out var factsEl))
        {
            if (factsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("'sceneFacts' must be a JSON array when present.");
            }

            foreach (var item in factsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Every element in 'sceneFacts' must be a JSON object.");
                }

                var keyRaw = ReadRequiredString(item, "factKey", "sceneFacts");
                if (!StoryEntityNormalizer.TryValidateTerm(keyRaw, "sceneFacts.factKey", out var factKey, out var normFactKey, out var keyErr))
                {
                    throw new InvalidOperationException(keyErr);
                }

                var typeRaw = ReadRequiredString(item, "factType", "sceneFacts");
                if (!StoryEntityNormalizer.TryParseSceneFactType(typeRaw, out var factType))
                {
                    throw new InvalidOperationException($"Invalid scene factType '{typeRaw}'.");
                }

                var summaryRaw = ReadRequiredString(item, "summary", "sceneFacts");
                if (!StoryEntityNormalizer.TryValidateOptionalText(summaryRaw, "sceneFacts.summary", StoryEntityNormalizer.MaxDescriptionLength, out var summary, out var sumErr) || string.IsNullOrEmpty(summary))
                {
                    throw new InvalidOperationException(sumErr ?? "sceneFacts.summary is required.");
                }

                var (evidenceSeg, quote) = ResolveAndValidateEvidence(item, "sceneFacts", byOrder, byStableId);

                if (!sceneFactsByKey.ContainsKey(normFactKey))
                {
                    sceneFactsByKey[normFactKey] = new ExtractedSceneFactCandidate(
                        factKey,
                        normFactKey,
                        factType,
                        summary,
                        evidenceSeg.Id,
                        evidenceSeg.StableId,
                        evidenceSeg.Order,
                        quote);
                }
            }
        }

        var speechByKey = new Dictionary<(string Speaker, string Addressee), ExtractedSpeechObservationCandidate>();
        if (root.TryGetProperty("speechObservations", out var speechEl))
        {
            if (speechEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("'speechObservations' must be a JSON array when present.");
            }

            foreach (var item in speechEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Every element in 'speechObservations' must be a JSON object.");
                }

                var speakerRaw = ReadRequiredString(item, "speakerName", "speechObservations");
                if (!StoryEntityNormalizer.TryValidateTerm(speakerRaw, "speechObservations.speakerName", out var speakerName, out var normSpeaker, out var spErr))
                {
                    throw new InvalidOperationException(spErr);
                }

                var addresseeRaw = ReadStrictOptionalString(item, "addresseeName", "speechObservations");
                string? addresseeName = null;
                var normAddressee = string.Empty;
                if (!string.IsNullOrWhiteSpace(addresseeRaw))
                {
                    if (!StoryEntityNormalizer.TryValidateTerm(addresseeRaw, "speechObservations.addresseeName", out var validAddressee, out var validNormAddr, out var addrErr))
                    {
                        throw new InvalidOperationException(addrErr);
                    }
                    addresseeName = validAddressee;
                    normAddressee = validNormAddr;
                }

                var regRaw = ReadRequiredString(item, "speechRegister", "speechObservations");
                if (!StoryEntityNormalizer.TryValidateOptionalText(regRaw, "speechObservations.speechRegister", StoryEntityNormalizer.MaxSpeechRegisterLength, out var register, out var regErr) || string.IsNullOrEmpty(register))
                {
                    throw new InvalidOperationException(regErr ?? "speechObservations.speechRegister is required.");
                }

                var pronounRaw = ReadStrictOptionalString(item, "pronounStyle", "speechObservations");
                if (!StoryEntityNormalizer.TryValidateOptionalText(pronounRaw, "speechObservations.pronounStyle", StoryEntityNormalizer.MaxTermLength, out var pronounStyle, out var proErr))
                {
                    throw new InvalidOperationException(proErr);
                }

                var obsRaw = ReadRequiredString(item, "observation", "speechObservations");
                if (!StoryEntityNormalizer.TryValidateOptionalText(obsRaw, "speechObservations.observation", StoryEntityNormalizer.MaxDescriptionLength, out var observation, out var obsErr) || string.IsNullOrEmpty(observation))
                {
                    throw new InvalidOperationException(obsErr ?? "speechObservations.observation is required.");
                }

                var (evidenceSeg, quote) = ResolveAndValidateEvidence(item, "speechObservations", byOrder, byStableId);

                var compositeKey = (normSpeaker, normAddressee);
                if (!speechByKey.ContainsKey(compositeKey))
                {
                    speechByKey[compositeKey] = new ExtractedSpeechObservationCandidate(
                        speakerName,
                        normSpeaker,
                        addresseeName,
                        normAddressee,
                        register,
                        pronounStyle,
                        observation,
                        evidenceSeg.Id,
                        evidenceSeg.StableId,
                        evidenceSeg.Order,
                        quote);
                }
            }
        }

        return (entitiesByKey.Values.ToList(), sceneFactsByKey.Values.ToList(), speechByKey.Values.ToList());
    }

    private static (SourceSegmentInput Segment, string? Quote) ResolveAndValidateEvidence(
        JsonElement item,
        string section,
        IReadOnlyDictionary<int, SourceSegmentInput> byOrder,
        IReadOnlyDictionary<string, SourceSegmentInput> byStableId)
    {
        int? order = null;
        if (item.TryGetProperty("evidenceSegmentOrder", out var ordProp) && ordProp.ValueKind != JsonValueKind.Null)
        {
            if (ordProp.ValueKind != JsonValueKind.Number || !ordProp.TryGetInt32(out var ordVal) || ordVal < 0)
            {
                throw new InvalidOperationException($"{section}.evidenceSegmentOrder must be a non-negative integer.");
            }
            order = ordVal;
        }
        else if (item.TryGetProperty("firstSeenOrder", out var legacyOrdProp) && legacyOrdProp.ValueKind != JsonValueKind.Null)
        {
            if (legacyOrdProp.ValueKind != JsonValueKind.Number || !legacyOrdProp.TryGetInt32(out var legVal) || legVal < 0)
            {
                throw new InvalidOperationException($"{section}.firstSeenOrder must be a non-negative integer.");
            }
            order = legVal;
        }

        var stableId = ReadStrictOptionalString(item, "evidenceSegmentStableId", section);

        if (!order.HasValue && string.IsNullOrWhiteSpace(stableId))
        {
            throw new InvalidOperationException($"{section} item must specify evidenceSegmentOrder or evidenceSegmentStableId.");
        }

        SourceSegmentInput? byOrdSeg = null;
        if (order.HasValue && !byOrder.TryGetValue(order.Value, out byOrdSeg))
        {
            throw new InvalidOperationException($"{section} referenced evidenceSegmentOrder {order.Value} which does not exist in the supplied source window.");
        }

        SourceSegmentInput? byStableSeg = null;
        if (!string.IsNullOrWhiteSpace(stableId) && !byStableId.TryGetValue(stableId, out byStableSeg))
        {
            throw new InvalidOperationException($"{section} referenced evidenceSegmentStableId '{stableId}' which does not exist in the supplied source window.");
        }

        if (byOrdSeg != null && byStableSeg != null && byOrdSeg.Id != byStableSeg.Id)
        {
            throw new InvalidOperationException($"{section} evidenceSegmentOrder ({order}) and evidenceSegmentStableId ('{stableId}') refer to different segments.");
        }

        var matched = byOrdSeg ?? byStableSeg!;

        var rawQuote = ReadStrictOptionalString(item, "evidenceQuote", section);
        if (!StoryEntityNormalizer.TryValidateOptionalText(rawQuote, $"{section}.evidenceQuote", StoryEntityNormalizer.MaxEvidenceQuoteLength, out var cleanQuote, out var qErr))
        {
            throw new InvalidOperationException(qErr);
        }

        if (!string.IsNullOrWhiteSpace(cleanQuote) && !StoryEntityNormalizer.MatchesNormalizedQuote(matched.Content, cleanQuote))
        {
            throw new InvalidOperationException($"{section}.evidenceQuote does not match content of segment order {matched.Order}.");
        }

        return (matched, cleanQuote);
    }

    private static string ExtractJsonObjectPayload(string raw)
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
            else
            {
                throw new InvalidOperationException("Unterminated markdown JSON fence.");
            }
        }

        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            throw new InvalidOperationException("Extraction response must be a valid JSON object.");
        }

        return trimmed;
    }

    private static string ReadRequiredString(JsonElement obj, string propName, string section)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{section}.{propName} is required and must be a string.");
        }

        var val = prop.GetString();
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new InvalidOperationException($"{section}.{propName} cannot be blank.");
        }

        return val;
    }

    private static string? ReadStrictOptionalString(JsonElement obj, string propName, string section)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{section}.{propName} must be a string or null.");
        }

        return prop.GetString();
    }

    private static IReadOnlyList<string> ReadStrictOptionalStringArray(JsonElement obj, string propName, string section)
    {
        if (!obj.TryGetProperty(propName, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (prop.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{section}.{propName} must be an array of strings.");
        }

        var list = new List<string>();
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"Every element of {section}.{propName} must be a string.");
            }
            if (el.GetString() is { } s)
            {
                list.Add(s);
            }
        }
        return list;
    }
}
