using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// Fetches the landing page's public progress snapshot (#309) through the typed TMS client — the
/// anonymous <c>GET /api/v1/progress</c>, so the page works before any login exists. Kept as a thin
/// injectable seam so the page's data flow is unit-testable end-to-end over a stubbed HTTP handler
/// (mirrors <see cref="Dashboard.DashboardStatsLoader"/>).
/// </summary>
internal sealed class HomeProgressLoader
{
    private const string ProgressRelativeUri = "/api/v1/progress";

    private readonly ITranslationSystemClient _client;

    public HomeProgressLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    public Task<ApiResult<PublicProgressResponse>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return _client.GetApiResultAsync<PublicProgressResponse>(ProgressRelativeUri, cancellationToken);
    }
}
