using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal interface IDiscoveryCache
{
    /// <summary>
    /// Returns the TMS HATEOAS discovery root, cached per auth state for a day. Pages resolve their
    /// API links from it instead of hardcoding routes.
    /// </summary>
    Task<ApiResult<TranslationDiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the auth server's HATEOAS discovery root, cached per auth state for a day. The
    /// account pages resolve <c>export-account-data</c> from it instead of hardcoding routes.
    /// </summary>
    Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);
}
