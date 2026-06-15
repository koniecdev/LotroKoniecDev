using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// Fetches the mini-dashboard's progress counters (M3-05) through the typed TMS client. Kept as a thin
/// injectable seam so the page's data flow is unit-testable end-to-end over a stubbed HTTP handler (the
/// Frontend has no bUnit for component-level rendering tests).
/// </summary>
internal sealed class DashboardStatsLoader
{
    private const string StatsRelativeUri = "/api/v1/translations/stats";

    private readonly ITranslationSystemClient _client;

    public DashboardStatsLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    public Task<ApiResult<TranslationStatsResponse>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return _client.GetApiResultAsync<TranslationStatsResponse>(StatsRelativeUri, cancellationToken);
    }
}
