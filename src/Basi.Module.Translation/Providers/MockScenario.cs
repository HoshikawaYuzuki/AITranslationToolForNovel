namespace Basi.Module.Translation.Providers;

/// <summary>
/// Defines the behavior the MockAiProvider should exhibit.
/// </summary>
public enum MockScenario
{
    /// <summary>Returns a valid AI response with realistic token counts.</summary>
    Success,

    /// <summary>Throws AiProviderError with the specified ErrorCode.</summary>
    Error,

    /// <summary>Delays for the configured duration then throws TimeoutException.</summary>
    Timeout,

    /// <summary>Throws AiProviderError with RateLimited error code.</summary>
    RateLimited,

    /// <summary>Returns malformed / unexpected content.</summary>
    InvalidOutput,

    /// <summary>Returns a response that is missing expected segment IDs.</summary>
    PartialResponse
}
