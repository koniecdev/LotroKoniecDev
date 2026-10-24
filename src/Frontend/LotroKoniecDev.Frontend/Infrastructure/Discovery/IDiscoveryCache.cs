using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal interface IDiscoveryCache
{
    /// <summary>Cached for a day per login state.</summary>
    Task<ApiResult<TranslationDiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Cached for a day per login state.</summary>
    Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default);
}
