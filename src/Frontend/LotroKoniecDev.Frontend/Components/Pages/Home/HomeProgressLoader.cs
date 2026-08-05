using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// Fetches the landing page's public progress snapshot (#309) through the typed TMS client. The entry
/// point comes from the service document's <c>progress</c> rel (#610) — that endpoint is anonymous, so
/// the page works before any login exists. Kept as a thin injectable seam so the page's data flow is
/// unit-testable end-to-end over a stubbed HTTP handler (mirrors <see cref="Dashboard.DashboardStatsLoader"/>).
/// </summary>
internal sealed class HomeProgressLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public HomeProgressLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<PublicProgressResponse>> LoadAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.Progress,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<PublicProgressResponse>(href.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<PublicProgressResponse>(href.Value, cancellationToken);
    }
}
