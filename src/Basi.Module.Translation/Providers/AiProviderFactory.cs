using Basi.Core.Interfaces;
using Basi.Core.Models;
using Basi.Module.Translation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Module.Translation.Providers;

public sealed class AiProviderFactory : IAiProviderFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApiKeyVault _apiKeyVault;
    private readonly IServiceProvider _serviceProvider;

    public AiProviderFactory(
        IHttpClientFactory httpClientFactory,
        IApiKeyVault apiKeyVault,
        IServiceProvider serviceProvider)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _apiKeyVault = apiKeyVault ?? throw new ArgumentNullException(nameof(apiKeyVault));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IAiProvider CreateProvider(string providerName, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var norm = providerName.Trim().ToLowerInvariant();
        return norm switch
        {
            "mock" => _serviceProvider.GetService<MockAiProvider>() ?? new MockAiProvider(),
            "gemini" => new GeminiProvider(_httpClientFactory.CreateClient("Gemini"), apiKey),
            "openai" => new OpenAiProvider(_httpClientFactory.CreateClient("OpenAI"), apiKey),
            _ => throw new ArgumentException($"Unsupported AI provider: '{providerName}'. Supported providers are: 'Gemini', 'OpenAI', 'Mock'.", nameof(providerName))
        };
    }

    public IAiProvider GetProviderForUser(Guid userId, string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var norm = providerName.Trim().ToLowerInvariant();
        if (norm == "mock")
        {
            return _serviceProvider.GetService<MockAiProvider>() ?? new MockAiProvider();
        }

        var key = _apiKeyVault.GetKey(userId, norm);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderError(
                AiErrorCode.InvalidApiKey,
                $"No API key found in RAM for provider '{providerName}'. Please provide your API key before starting translation.");
        }

        return CreateProvider(providerName, key);
    }

    public IAiProvider GetProviderForJob(Guid userId, string providerName, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var norm = providerName.Trim().ToLowerInvariant();
        if (norm == "mock")
        {
            return _serviceProvider.GetService<MockAiProvider>() ?? new MockAiProvider();
        }

        // Job-bound key takes priority (with ownership check); fall back to session/user-bound key
        var key = _apiKeyVault.GetJobKey(userId, jobId) ?? _apiKeyVault.GetKey(userId, norm);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderError(
                AiErrorCode.InvalidApiKey,
                $"No API key found in RAM for provider '{providerName}'. Please provide your API key before starting translation.");
        }

        return CreateProvider(providerName, key);
    }
}
