using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// The game-versions page's calls to the TMS. The <c>register</c> and <c>delete</c> rels are both the
/// permission and the URL to call (#158). A thin class, so a bUnit test can drive the page through a
/// substituted loader.
/// </summary>
internal sealed class GameVersionsLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public GameVersionsLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<CollectionResponse<GameVersionResponse>>> ListGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.GameVersions,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<CollectionResponse<GameVersionResponse>>(href.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<CollectionResponse<GameVersionResponse>>(
            href.Value,
            cancellationToken);
    }

    public Task<ApiResult<GameVersionResponse>> RegisterGameVersionAsync(
        string registerHref,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registerHref);

        return _client.PostApiResultAsync<GameVersionResponse>(
            registerHref,
            new RegisterGameVersionRequest(version),
            cancellationToken);
    }

    public Task<ApiResult> DeleteGameVersionAsync(
        string deleteHref,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteHref);

        return _client.DeleteApiResultAsync(deleteHref, cancellationToken);
    }
}
