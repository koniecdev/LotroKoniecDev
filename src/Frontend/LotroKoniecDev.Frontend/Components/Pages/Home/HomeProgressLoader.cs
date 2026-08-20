using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// Fetches the landing page's public progress numbers (#309) through the typed TMS client. The endpoint
/// comes from the service document's <c>progress</c> rel (#610) and is open to anyone, so the page works
/// before anybody logs in.
/// It stays a thin injectable class, so the page's data flow can be unit-tested end to end against a
/// stubbed HTTP handler, like <see cref="Dashboard.DashboardStatsLoader"/>.
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
