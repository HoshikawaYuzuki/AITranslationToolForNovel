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

    public AiProviderError(AiErrorCode errorCode, string? message = null, Exception? innerException = null)
        : base(message ?? $"AI Provider error: {errorCode}", innerException)
    {
        ErrorCode = errorCode;
    }
}
