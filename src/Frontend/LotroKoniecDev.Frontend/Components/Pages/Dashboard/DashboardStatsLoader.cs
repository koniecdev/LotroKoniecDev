using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// Fetches the mini-dashboard's progress counters (M3-05) through the typed TMS client. It finds the
/// endpoint through the service document's <c>translation-stats</c> rel (#610), which the API only
/// offers to a translator, so a session without that right gets a clear failure instead of a guessed
/// call.
/// It stays a thin injectable class, so the page's data flow can be unit-tested end to end against a
/// stubbed HTTP handler.
/// </summary>
internal sealed class DashboardStatsLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public DashboardStatsLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<TranslationStatsResponse>> LoadAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.TranslationStats,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<TranslationStatsResponse>(href.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<TranslationStatsResponse>(href.Value, cancellationToken);
    }
}
