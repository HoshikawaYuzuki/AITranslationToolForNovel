using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

public interface ITranslationEngine
{
    Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(
        IAiProvider provider,
        string model,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentItem> batch,
        IReadOnlyList<ContextSegmentItem>? context = null,
        CancellationToken ct = default);
}

public sealed class TranslationEngine : ITranslationEngine
{
    private readonly ITranslationPromptBuilder _promptBuilder;

    public TranslationEngine(ITranslationPromptBuilder promptBuilder)
    {
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
    }

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(
        IAiProvider provider,
        string model,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentItem> batch,
        IReadOnlyList<ContextSegmentItem>? context = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return new Dictionary<int, string>();

        var prompt = _promptBuilder.BuildPrompt(sourceLanguage, targetLanguage, batch, context);

        var request = new AiRequest(
            Model: model,
            SystemPrompt: prompt.SystemPrompt,
            UserPrompt: prompt.UserPrompt,
            MaxTokens: 4096,
            Temperature: 0.7
        );

        var response = await provider.CompleteAsync(request, ct);

        var expectedOrders = batch.Select(b => b.Order).ToList();
        var parsed = _promptBuilder.ParseResponse(response.Content, expectedOrders);

        // Fallback: If only 1 segment was sent and model returned untagged text
        if (batch.Count == 1 && !parsed.ContainsKey(batch[0].Order) && !string.IsNullOrWhiteSpace(response.Content))
        {
            return new Dictionary<int, string>
            {
                [batch[0].Order] = response.Content.Trim()
            };
        }

        return parsed;
    }
}
