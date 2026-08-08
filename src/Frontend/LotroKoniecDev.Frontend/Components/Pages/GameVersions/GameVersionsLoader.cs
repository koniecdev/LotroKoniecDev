using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// Drives the game-versions page's TMS calls through the typed client (#209). The list is the one entry
/// point, resolved from the service document's <c>game-versions</c> rel (#610); everything past it is
/// link-driven — the collection envelope's <c>register</c> rel and each item's <c>delete</c> rel are
/// both the affordance gate and the URI to call (#158), so the server alone decides who may do what and
/// where. Kept as a thin injectable seam so the page's data flow is unit-testable end-to-end over a
/// stubbed HTTP handler and so a bUnit render test can drive the page through a substituted loader.
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
    /// Lists every known version as the HATEOAS collection envelope, <b>keeping its <c>Links</c></b> so
    /// the page can gate — and address — the admin <c>register</c> / per-item <c>delete</c> actions from
    /// the server-advertised rels rather than recomputing the role locally.
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
    /// Registers a version manually (#107) by POSTing to the collection's <c>register</c> link.
    /// <paramref name="registerHref"/> is the server-advertised URI — emitted admin-only, never a
    /// FE-constructed path.
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
    /// Deletes a mistaken entry by following its own <c>delete</c> link.
    /// <paramref name="deleteHref"/> is the server-advertised URI — present only for an admin on a row
    /// no import has landed against (#624), which is the whole gate.
    /// </summary>
    public Task<ApiResult> DeleteGameVersionAsync(
        string deleteHref,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteHref);

        return _client.DeleteApiResultAsync(deleteHref, cancellationToken);
    }
}
