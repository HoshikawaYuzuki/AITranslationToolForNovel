using Basi.Core.Interfaces;

namespace Basi.Module.Translation.Providers;

public interface IAiProviderFactory
{
    /// <summary>Creates an IAiProvider instance directly with a provider name and API key.</summary>
    IAiProvider CreateProvider(string providerName, string apiKey);

    /// <summary>Resolves an IAiProvider for a specific user using their RAM-stored key.</summary>
    IAiProvider GetProviderForUser(Guid userId, string providerName);

    /// <summary>Resolves an IAiProvider for a specific Job, preferring Job-scoped RAM key over user-scoped RAM key.</summary>
    IAiProvider GetProviderForJob(Guid userId, string providerName, Guid jobId);
}
