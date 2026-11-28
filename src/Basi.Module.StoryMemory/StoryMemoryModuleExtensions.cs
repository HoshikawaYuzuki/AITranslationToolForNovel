using Basi.Module.StoryMemory.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Basi.Module.StoryMemory;

public static class StoryMemoryModuleExtensions
{
    public static IServiceCollection AddStoryMemoryModule(this IServiceCollection services)
    {
        services.AddSingleton<IStoryMemorySelector, StoryMemorySelector>();
        services.AddSingleton<IStoryMemoryExtractor, StoryMemoryExtractor>();
        services.AddSingleton<StoryMemoryMergeGate>();
        return services;
    }
}
