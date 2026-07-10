using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Builds a fresh in-memory (L1-only) <see cref="HybridCache"/> per test: the default real
/// implementation is a purely in-process memory store, so unit tests stay pure (no I/O) while
/// exercising the genuine cache semantics (entry options, stampede protection, eviction).
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
