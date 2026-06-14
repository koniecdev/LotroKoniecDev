using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal interface IDiscoveryCache
{
    /// <summary>
    /// Returns the TMS HATEOAS discovery root, cached per auth state for a day. Pages resolve their
    /// API links from it instead of hardcoding routes.
    /// </summary>
    Task<ApiResult<DiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);
}
