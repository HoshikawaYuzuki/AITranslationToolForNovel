using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Providers;

public class MockAiProviderOptions
{
    public MockScenario Scenario { get; set; } = MockScenario.Success;
    public AiErrorCode ErrorCode { get; set; } = AiErrorCode.Unknown;
    public TimeSpan TimeoutDelay { get; set; } = TimeSpan.FromSeconds(5);
}

public class MockAiProvider : IAiProvider
{
    private readonly MockAiProviderOptions _options;

    public MockAiProvider(MockAiProviderOptions? options = null)
    {
        _options = options ?? new MockAiProviderOptions();
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        return _options.Scenario switch
        {
            MockScenario.Success => await HandleSuccess(request, ct),
            MockScenario.Error => throw new AiProviderError(_options.ErrorCode, $"Mock error: {_options.ErrorCode}"),
            MockScenario.Timeout => await HandleTimeout(ct),
            MockScenario.RateLimited => throw new AiProviderError(AiErrorCode.RateLimited, "Rate limit exceeded. Please retry after 60 seconds."),
            MockScenario.InvalidOutput => HandleInvalidOutput(request),
            MockScenario.PartialResponse => HandlePartialResponse(request),
            _ => throw new ArgumentOutOfRangeException(nameof(_options.Scenario))
        };
    }

    private Task<AiResponse> HandleSuccess(AiRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var promptTokens = EstimateTokens(request.SystemPrompt) + EstimateTokens(request.UserPrompt);
        var completionTokens = 150; // Realistic mock count

        string content;
        var promptText = request.UserPrompt;
        const string segmentsMarker = "--- SEGMENTS TO TRANSLATE ---";
        const string storyMemoryMarker = "--- STORY MEMORY EXTRACTION ---";
        var markerIndex = promptText.IndexOf(segmentsMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            // Only translate target segments under the marker, ignoring any preceding context segments
            promptText = promptText.Substring(markerIndex + segmentsMarker.Length);
        }

        var segMatches = markerIndex >= 0
            ? System.Text.RegularExpressions.Regex.Matches(promptText, @"\[SEG:(\d+)\]\s*([^\r\n]+)")
            : System.Text.RegularExpressions.Regex.Matches(string.Empty, @"\[SEG:(\d+)\]");

        if (markerIndex < 0 && request.UserPrompt.Contains(storyMemoryMarker, StringComparison.OrdinalIgnoreCase))
        {
            content = BuildMockStoryMemoryJson(request.UserPrompt);
        }
        else if (segMatches.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (System.Text.RegularExpressions.Match match in segMatches)
            {
                var order = match.Groups[1].Value;
                var text = match.Groups[2].Value.Trim();
                sb.AppendLine($"[SEG:{order}] [Translated] {text}");
            }
            content = sb.ToString().TrimEnd();
        }
        else
        {
            content = "This is a translated segment. [seg-001] The hero ventured into the dark forest. [seg-002] A mysterious voice echoed through the trees.";
        }

        var response = new AiResponse(
            Content: content,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            TotalTokens: promptTokens + completionTokens,
            ModelUsed: request.Model,
            FinishReason: FinishReason.Stop
        );

        return Task.FromResult(response);
    }

    private async Task<AiResponse> HandleTimeout(CancellationToken ct)
    {
        await Task.Delay(_options.TimeoutDelay, ct);
        throw new TimeoutException($"AI provider request timed out after {_options.TimeoutDelay.TotalSeconds}s");
    }

    private AiResponse HandleInvalidOutput(AiRequest request)
    {
        // Returns malformed content that a parser would choke on
        return new AiResponse(
            Content: "<<<MALFORMED>>> {broken json: [unterminated, \x00\x01 @@@ END",
            PromptTokens: 50,
            CompletionTokens: 10,
            TotalTokens: 60,
            ModelUsed: request.Model,
            FinishReason: FinishReason.Error
        );
    }

    private AiResponse HandlePartialResponse(AiRequest request)
    {
        // Returns content missing expected segment IDs (only seg-001, missing seg-002 and seg-003)
        return new AiResponse(
            Content: "[seg-001] The hero ventured into the dark forest.",
            PromptTokens: 80,
            CompletionTokens: 30,
            TotalTokens: 110,
            ModelUsed: request.Model,
            FinishReason: FinishReason.MaxTokens
        );
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Rough estimate: ~4 chars per token
        return Math.Max(1, text.Length / 4);
    }

    private static string BuildMockStoryMemoryJson(string userPrompt)
    {
        const string novelSegmentsMarker = "--- NOVEL SEGMENTS ---";
        var idx = userPrompt.IndexOf(novelSegmentsMarker, StringComparison.OrdinalIgnoreCase);
        var slice = idx >= 0 ? userPrompt[(idx + novelSegmentsMarker.Length)..] : userPrompt;

        var targetLangMatch = System.Text.RegularExpressions.Regex.Match(userPrompt, @"TargetLanguage:\s*([^\r\n]+)");
        var targetLang = targetLangMatch.Success ? targetLangMatch.Groups[1].Value.Trim() : "target";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<object>();

        foreach (System.Text.RegularExpressions.Match segMatch in System.Text.RegularExpressions.Regex.Matches(
                     slice,
                     @"\[SEG:(\d+)\]\s*([^\r\n]+)"))
        {
            if (!int.TryParse(segMatch.Groups[1].Value, out var order)) order = 0;
            var text = segMatch.Groups[2].Value.Trim();

            foreach (System.Text.RegularExpressions.Match quoted in System.Text.RegularExpressions.Regex.Matches(
                         text,
                         @"[「『""]([^{}「」『""\r\n]{2,32})[」』""]"))
            {
                var term = quoted.Groups[1].Value.Trim();
                if (seen.Add(term))
                {
                    entities.Add(new
                    {
                        sourceTerm = term,
                        translatedTerm = $"[{targetLang}] {term}",
                        category = "Character",
                        aliases = Array.Empty<string>(),
                        description = "Mock extracted proper term.",
                        firstSeenOrder = order
                    });
                }
            }

            foreach (System.Text.RegularExpressions.Match prop in System.Text.RegularExpressions.Regex.Matches(
                         text,
                         @"\b([A-Z][a-zA-Z0-9]{2,}(?:\s+[A-Z][a-zA-Z0-9]{2,})?)\b"))
            {
                var term = prop.Groups[1].Value.Trim();
                if (term is "The" or "This" or "That" or "There" or "When" or "After" or "Before") continue;
                if (seen.Add(term))
                {
                    entities.Add(new
                    {
                        sourceTerm = term,
                        translatedTerm = $"[{targetLang}] {term}",
                        category = "Character",
                        aliases = Array.Empty<string>(),
                        description = "Mock extracted proper noun.",
                        firstSeenOrder = order
                    });
                }
            }
        }

        if (entities.Count == 0)
        {
            var firstSeg = System.Text.RegularExpressions.Regex.Match(slice, @"\[SEG:(\d+)\]\s*([^\r\n]+)");
            var firstOrder = firstSeg.Success && int.TryParse(firstSeg.Groups[1].Value, out var o) ? o : 0;
            var sample = firstSeg.Success
                ? firstSeg.Groups[2].Value.Trim().Split(new[] { ' ', '　', '。', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Protagonist"
                : "Protagonist";
            entities.Add(new
            {
                sourceTerm = sample,
                translatedTerm = $"[{targetLang}] {sample}",
                category = "Concept",
                aliases = Array.Empty<string>(),
                description = "Deterministic mock fallback entity.",
                firstSeenOrder = firstOrder
            });
        }

        return System.Text.Json.JsonSerializer.Serialize(new { entities = entities.Take(10) });
    }
}
