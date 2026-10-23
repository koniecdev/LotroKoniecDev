using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

public static class DiscoveryDependencyInjectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddDiscoveryCache()
        {
            // In memory only: no IDistributedCache is registered. Discovery responses are small, and a
            // shared cache would only add latency.
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

            // Scoped, so it never holds a typed HTTP client past its handler recycling. HybridCache is a
            // singleton, so an entry still lives across requests.
            services.AddScoped<IDiscoveryCache, DiscoveryCache>();

            return services;
        }
    }
}
