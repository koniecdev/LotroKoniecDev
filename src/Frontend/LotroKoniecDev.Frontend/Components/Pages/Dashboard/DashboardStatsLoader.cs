using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// Fetches the mini-dashboard's progress counters (M3-05) through the typed TMS client, resolving the
/// entry point from the service document's <c>translation-stats</c> rel (#610) — a rel the API emits
/// only for a translator, so an unauthorized session gets a clear failure instead of a guessed call.
/// Kept as a thin injectable seam so the page's data flow is unit-testable end-to-end over a stubbed
/// HTTP handler (the Frontend has no bUnit for component-level rendering tests).
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
