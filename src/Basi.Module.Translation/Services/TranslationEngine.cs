using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

public record TranslationBatchResult(
    IReadOnlyDictionary<int, string> Translations,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string ModelUsed);

public interface ITranslationEngine
{
    Task<TranslationBatchResult> TranslateBatchAsync(
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

    public async Task<TranslationBatchResult> TranslateBatchAsync(
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
        if (batch.Count == 0)
        {
            return new TranslationBatchResult(
                new Dictionary<int, string>(),
                0,
                0,
                0,
                model);
        }

        var prompt = _promptBuilder.BuildPrompt(sourceLanguage, targetLanguage, batch, context);

        var request = new AiRequest(
            Model: model,
            SystemPrompt: prompt.SystemPrompt,
            UserPrompt: prompt.UserPrompt,
            MaxTokens: 4096,
            Temperature: 0.7
        );

        var response = await provider.CompleteAsync(request, ct);

        // F03: Do not treat truncated outputs as successful completions
        if (response.FinishReason == FinishReason.MaxTokens)
        {
            throw new AiProviderError(AiErrorCode.Unknown, "Model response was truncated because MaxTokens limit was reached.");
        }
        if (response.FinishReason == FinishReason.ContentFiltered)
        {
            throw new AiProviderError(AiErrorCode.ContentFiltered, "Model response was blocked by content filter.");
        }

        var expectedOrders = batch.Select(b => b.Order).ToList();
        var parsed = _promptBuilder.ParseResponse(response.Content, expectedOrders);

        // Fallback: If only 1 segment was sent and model returned pure untagged text without [SEG: tags
        IReadOnlyDictionary<int, string> finalTranslations = parsed;
        if (batch.Count == 1
            && parsed.Count == 0
            && !string.IsNullOrWhiteSpace(response.Content)
            && !response.Content.Contains("[SEG:", StringComparison.OrdinalIgnoreCase))
        {
            finalTranslations = new Dictionary<int, string>
            {
                [batch[0].Order] = response.Content.Trim()
            };
        }

        return new TranslationBatchResult(
            Translations: finalTranslations,
            PromptTokens: response.PromptTokens,
            CompletionTokens: response.CompletionTokens,
            TotalTokens: response.TotalTokens,
            ModelUsed: response.ModelUsed
        );
    }
}
