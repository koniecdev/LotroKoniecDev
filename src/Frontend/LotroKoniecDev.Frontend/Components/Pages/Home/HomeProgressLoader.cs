using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// The <c>progress</c> rel is open to anyone, so the landing page works before anybody signs in (#309).
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
