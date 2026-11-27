using Basi.Core.Interfaces;
using Basi.Core.Models;

namespace Basi.Module.Translation.Services;

public record TranslationBatchResult(
    IReadOnlyDictionary<int, string> Translations,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string ModelUsed);

public class TranslationBatchException : Exception
{
    public int PromptTokens { get; }
    public int CompletionTokens { get; }
    public int TotalTokens { get; }
    public string ModelUsed { get; }

    public TranslationBatchException(string message, int promptTokens, int completionTokens, int totalTokens, string modelUsed)
        : base(message)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        ModelUsed = modelUsed;
    }
}

public interface ITranslationEngine
{
    Task<TranslationBatchResult> TranslateBatchAsync(
        IAiProvider provider,
        string model,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentItem> batch,
        IReadOnlyList<ContextSegmentItem>? context = null,
        CancellationToken ct = default,
        int? maxOutputTokens = null);
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
        CancellationToken ct = default,
        int? maxOutputTokens = null)
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
            MaxTokens: Math.Clamp(maxOutputTokens ?? 4096, 1, 4096),
            Temperature: 0.7
        );

        AiResponse response;
        try
        {
            response = await provider.CompleteAsync(request, ct);
        }
        catch (AiProviderError ex) when (ex.ErrorCode != AiErrorCode.InvalidApiKey)
        {
            throw new TranslationBatchException(
                ex.Message,
                ex.PromptTokens,
                ex.CompletionTokens,
                ex.TotalTokens,
                ex.ModelUsed ?? model);
        }

        // F03 & R11: Only accept successful completion (FinishReason.Stop). Carry token usage in exception!
        if (response.FinishReason == FinishReason.MaxTokens)
        {
            throw new TranslationBatchException(
                "Model response was truncated because MaxTokens limit was reached.",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }
        if (response.FinishReason == FinishReason.ContentFiltered)
        {
            throw new TranslationBatchException(
                "Model response was blocked by content filter.",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }
        if (response.FinishReason != FinishReason.Stop)
        {
            throw new TranslationBatchException(
                $"Model completion did not complete successfully (FinishReason: {response.FinishReason}).",
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

        var expectedOrders = batch.Select(b => b.Order).ToList();
        IReadOnlyDictionary<int, string> parsed;
        try
        {
            parsed = _promptBuilder.ParseResponse(response.Content, expectedOrders);
        }
        catch (InvalidOperationException ex)
        {
            // Contract violation (duplicate or unexpected tag) - carry usage metadata
            throw new TranslationBatchException(
                ex.Message,
                response.PromptTokens, response.CompletionTokens, response.TotalTokens, response.ModelUsed);
        }

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
