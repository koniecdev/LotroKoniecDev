using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal interface IDiscoveryCache
{
    /// <summary>
    /// Returns the TMS discovery root, cached for a day per login state. Pages take their API links from
    /// it instead of holding routes in code.
    /// </summary>
    Task<ApiResult<TranslationDiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the auth server's discovery root, cached for a day per login state. The account pages take
    /// <c>export-account-data</c> from it instead of holding routes in code.
    /// </summary>
    Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);
}
