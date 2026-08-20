using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

public static class DiscoveryDependencyInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDiscoveryCache()
        {
            // An in-memory HybridCache only. No IDistributedCache is registered, so it uses its
            // in-memory store. Discovery responses are small and belong to this process, and a shared
            // cache would only add latency.
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

            // Scoped, because the typed HTTP client it uses is scoped to a request. HybridCache itself is
            // a singleton inside, so an entry still lives across requests.
            services.AddScoped<IDiscoveryCache, DiscoveryCache>();

            return services;
        }
    }
}
