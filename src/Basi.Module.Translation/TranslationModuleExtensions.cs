using Basi.Core.Interfaces;
using Basi.Module.Translation.Providers;
using Basi.Module.Translation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Module.Translation;

public static class TranslationModuleExtensions
{
    public static IServiceCollection AddTranslationModule(this IServiceCollection services, bool useMockProvider = false)
    {
        services.AddHttpClient();
        services.AddSingleton<IApiKeyVault, InMemoryApiKeyVault>();
        services.AddSingleton<IAiProviderFactory, AiProviderFactory>();
        services.AddSingleton<ITranslationPromptBuilder, TranslationPromptBuilder>();
        services.AddSingleton<ITranslationEngine, TranslationEngine>();
        services.AddSingleton<TxtParser>();

        if (useMockProvider)
        {
            services.AddSingleton(new MockAiProviderOptions { Scenario = MockScenario.Success });
            services.AddSingleton<MockAiProvider>();
            services.AddSingleton<IAiProvider, MockAiProvider>();
        }

        return services;
    }
}
