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

    // jobId -> (userId, apiKey)
    private readonly ConcurrentDictionary<Guid, (Guid UserId, string ApiKey)> _jobKeys = new();

    // userId -> ConcurrentDictionary<jobId, byte>
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _userJobs = new();

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
        RemoveJobKeysForUser(userId);
    }

    public void SetJobKey(Guid userId, Guid jobId, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _jobKeys[jobId] = (userId, apiKey.Trim());
        _userJobs.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, byte>()).TryAdd(jobId, 0);
    }

    public void SetJobKey(Guid jobId, string apiKey)
    {
        SetJobKey(Guid.Empty, jobId, apiKey);
    }

    public string? GetJobKey(Guid jobId)
    {
        return _jobKeys.TryGetValue(jobId, out var entry) ? entry.ApiKey : null;
    }

    public string? GetJobKey(Guid userId, Guid jobId)
    {
        if (_jobKeys.TryGetValue(jobId, out var entry))
        {
            if (entry.UserId == Guid.Empty || entry.UserId == userId)
            {
                return entry.ApiKey;
            }
        }
        return null;
    }

    public bool HasJobKey(Guid jobId)
    {
        return _jobKeys.ContainsKey(jobId);
    }

    public bool RemoveJobKey(Guid jobId)
    {
        if (_jobKeys.TryRemove(jobId, out var entry))
        {
            if (entry.UserId != Guid.Empty && _userJobs.TryGetValue(entry.UserId, out var set))
            {
                set.TryRemove(jobId, out _);
            }
            return true;
        }
        return false;
    }

    public void RemoveJobKeysForUser(Guid userId)
    {
        if (_userJobs.TryRemove(userId, out var jobs))
        {
            foreach (var jobId in jobs.Keys)
            {
                _jobKeys.TryRemove(jobId, out _);
            }
        }
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();
}
