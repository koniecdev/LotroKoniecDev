using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

public static class DiscoveryDependencyInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDiscoveryCache()
        {
            // L1-only HybridCache: no IDistributedCache is registered, so HybridCache falls back to its
            // in-memory store. Discovery responses are tiny and tied to the local process; a distributed
            // cache would add latency for no gain.
            services.AddHybridCache(options =>
            {
                options.MaximumPayloadBytes = 1024 * 64;
                options.MaximumKeyLength = 256;
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromDays(1),
                    LocalCacheExpiration = TimeSpan.FromDays(1)
                };
            });

            // Scoped because the typed HTTP client it depends on is scoped (per request). HybridCache
            // itself is a singleton internally, so the TTL still spans requests.
            services.AddScoped<IDiscoveryCache, DiscoveryCache>();

            return services;
        }
    }
}
