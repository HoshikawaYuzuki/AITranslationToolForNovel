namespace Basi.Core.Models;

public enum AiErrorCode
{
    RateLimited,
    InvalidApiKey,
    ModelNotFound,
    ContentFiltered,
    NetworkError,
    Unknown
}

public class AiProviderError : Exception
{
    public AiErrorCode ErrorCode { get; }
    public int PromptTokens { get; }
    public int CompletionTokens { get; }
    public int TotalTokens { get; }
    public string? ModelUsed { get; }

    public AiProviderError(
        AiErrorCode errorCode,
        string? message = null,
        Exception? innerException = null,
        int promptTokens = 0,
        int completionTokens = 0,
        int totalTokens = 0,
        string? modelUsed = null)
        : base(message ?? $"AI Provider error: {errorCode}", innerException)
    {
        ErrorCode = errorCode;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens > 0 ? totalTokens : (promptTokens + completionTokens);
        ModelUsed = modelUsed;
    }
}
