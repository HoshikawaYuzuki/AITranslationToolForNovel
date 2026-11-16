using Basi.Core.Interfaces;
using Basi.Module.Translation.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Module.Translation;

public static class TranslationModuleExtensions
{
    public static IServiceCollection AddTranslationModule(this IServiceCollection services, bool useMockProvider = false)
    {
        if (useMockProvider)
        {
            services.AddSingleton(new MockAiProviderOptions { Scenario = MockScenario.Success });
            services.AddSingleton<IAiProvider, MockAiProvider>();
        }

        return services;
    }
}
