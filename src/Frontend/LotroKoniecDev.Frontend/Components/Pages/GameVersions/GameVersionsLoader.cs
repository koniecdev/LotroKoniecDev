using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// Makes the game-versions page's calls to the TMS through the typed client (#209). The list is the only
/// entry point and comes from the service document's <c>game-versions</c> rel (#610). Everything after
/// that follows links: the collection's <c>register</c> rel and each item's <c>delete</c> rel are both
/// the permission and the URL to call (#158), so the server alone decides who may do what and where.
/// It stays a thin injectable class, so the page's data flow can be unit-tested end to end against a
/// stubbed HTTP handler and a bUnit render test can drive the page through a substituted loader.
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

    /// <summary>
    /// Lists every known version and <b>keeps the <c>Links</c></b> the API sent, so the page can decide
    /// whether to show the admin <c>register</c> and per-item <c>delete</c> actions, and where to send
    /// them, from those links instead of working the role out itself.
    /// </summary>
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

    /// <summary>
    /// Registers a version by hand (#107) with a POST to the collection's <c>register</c> link.
    /// <paramref name="registerHref"/> is the URI the server sent, which it only sends to an admin, and
    /// never a path built here.
    /// </summary>
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

    /// <summary>
    /// Deletes an entry added by mistake by following its own <c>delete</c> link.
    /// <paramref name="deleteHref"/> is the URI the server sent, and it is only there for an admin on a
    /// row no import has run against (#624). That is the whole permission check.
    /// </summary>
    public Task<ApiResult> DeleteGameVersionAsync(
        string deleteHref,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteHref);

        return _client.DeleteApiResultAsync(deleteHref, cancellationToken);
    }
}
