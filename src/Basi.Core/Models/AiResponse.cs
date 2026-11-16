namespace Basi.Core.Models;

public record AiResponse(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string ModelUsed,
    FinishReason FinishReason
);
