using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// The real <see cref="HybridCache"/> with no distributed store is in-process only, so tests stay pure
/// while exercising the genuine cache behaviour.
/// </summary>
internal static class TestHybridCache
{
    public static HybridCache Create()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
