namespace Basi.Module.Translation.Services;

/// <summary>
/// Thread-safe in-memory vault for user API keys (BYOK).
/// Per ADR-004, keys are kept strictly in RAM and never written to disk, database, or logs.
/// </summary>
public interface IApiKeyVault
{
    /// <summary>Sets or updates an API key in RAM for a user and provider.</summary>
    void SetKey(Guid userId, string provider, string apiKey);

    /// <summary>Retrieves the API key for a user and provider from RAM.</summary>
    string? GetKey(Guid userId, string provider);

    /// <summary>Checks whether a key is present in RAM for a user and provider.</summary>
    bool HasKey(Guid userId, string provider);

    /// <summary>Returns a dictionary of known providers and whether an active key exists (never returns raw keys).</summary>
    IReadOnlyDictionary<string, bool> GetKeyStatus(Guid userId);

    /// <summary>Removes a specific provider key from RAM for a user.</summary>
    bool RemoveKey(Guid userId, string provider);

    /// <summary>Clears all keys for a user from RAM.</summary>
    void ClearAll(Guid userId);

    /// <summary>Sets a RAM-only API key bound specifically to a user and single Job lifecycle.</summary>
    void SetJobKey(Guid userId, Guid jobId, string apiKey);

    /// <summary>Sets a RAM-only API key bound specifically to a single Job lifecycle (legacy/shorthand).</summary>
    void SetJobKey(Guid jobId, string apiKey);

    /// <summary>Retrieves the RAM-only API key bound to a single Job, if any.</summary>
    string? GetJobKey(Guid jobId);

    /// <summary>Retrieves the RAM-only API key bound to a single Job with ownership check.</summary>
    string? GetJobKey(Guid userId, Guid jobId);

    /// <summary>Checks whether a RAM-only API key exists for a specific Job.</summary>
    bool HasJobKey(Guid jobId);

    /// <summary>Removes the RAM-only API key bound to a specific Job.</summary>
    bool RemoveJobKey(Guid jobId);

    /// <summary>Removes all Job-bound RAM keys belonging to a specific user.</summary>
    void RemoveJobKeysForUser(Guid userId);
}
