using System.Collections.Concurrent;

namespace Basi.Module.Translation.Services;

/// <summary>
/// In-memory implementation of IApiKeyVault using concurrent dictionary structures.
/// Adheres strictly to ADR-004: RAM-only, non-persistent, zero credential leakage.
/// </summary>
public sealed class InMemoryApiKeyVault : IApiKeyVault
{
    private static readonly string[] SupportedProviders = ["gemini", "openai"];

    // userId -> (normalizedProvider -> apiKey)
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>> _vault = new();

    // jobId -> apiKey (Job-bound RAM keys)
    private readonly ConcurrentDictionary<Guid, string> _jobKeys = new();

    public void SetKey(Guid userId, string provider, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var userKeys = _vault.GetOrAdd(userId, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        userKeys[NormalizeProvider(provider)] = apiKey.Trim();
    }

    public string? GetKey(Guid userId, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;

        if (_vault.TryGetValue(userId, out var userKeys)
            && userKeys.TryGetValue(NormalizeProvider(provider), out var key))
        {
            return key;
        }

        return null;
    }

    public bool HasKey(Guid userId, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;

        return _vault.TryGetValue(userId, out var userKeys)
            && userKeys.ContainsKey(NormalizeProvider(provider));
    }

    public IReadOnlyDictionary<string, bool> GetKeyStatus(Guid userId)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var userKeys = _vault.TryGetValue(userId, out var keys) ? keys : null;
        foreach (var p in SupportedProviders)
        {
            result[p] = userKeys != null && userKeys.ContainsKey(p);
        }

        return result;
    }

    public bool RemoveKey(Guid userId, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return false;

        if (_vault.TryGetValue(userId, out var userKeys))
        {
            return userKeys.TryRemove(NormalizeProvider(provider), out _);
        }

        return false;
    }

    public void ClearAll(Guid userId)
    {
        _vault.TryRemove(userId, out _);
    }

    public void SetJobKey(Guid jobId, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _jobKeys[jobId] = apiKey.Trim();
    }

    public string? GetJobKey(Guid jobId)
    {
        return _jobKeys.TryGetValue(jobId, out var key) ? key : null;
    }

    public bool HasJobKey(Guid jobId)
    {
        return _jobKeys.ContainsKey(jobId);
    }

    public bool RemoveJobKey(Guid jobId)
    {
        return _jobKeys.TryRemove(jobId, out _);
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();
}
