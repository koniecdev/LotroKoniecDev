using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Shared;

/// <summary>
/// The real registry over the frontend's own cache registration. A test then runs under the production
/// key limit, and can mark one device and read another.
/// </summary>
internal static class DeadSessionRegistryFactory
{
    internal static DeadSessionRegistry Create()
    {
        ServiceCollection services = new();
        services.AddDiscoveryCache();
        HybridCache hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new DeadSessionRegistry(hybridCache);
    }
}
