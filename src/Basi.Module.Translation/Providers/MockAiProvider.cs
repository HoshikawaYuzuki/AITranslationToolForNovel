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
        var segMatches = System.Text.RegularExpressions.Regex.Matches(
            request.UserPrompt,
            @"\[SEG:(\d+)\]\s*([^\r\n]+)");

        if (segMatches.Count > 0)
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
}
