using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.GameVersions;

/// <summary>
/// Drives the game-versions page's TMS calls through the typed client (#209): list every known
/// version as the HATEOAS collection envelope — <b>keeping its <c>Links</c></b> so the page can gate
/// the admin <c>register</c> / per-item <c>delete</c> affordances on the server-advertised rels (#158)
/// rather than recomputing the role locally — register one manually (<c>POST /api/v1/game-versions</c>,
/// #107) and delete a still-unprocessed mistaken entry (<c>DELETE /api/v1/game-versions/{id}</c>).
/// Kept as a thin injectable seam so the page's data flow is unit-testable end-to-end over a stubbed
/// HTTP handler and so a bUnit render test can drive the page through a substituted loader.
/// </summary>
internal sealed class GameVersionsLoader
{
    private const string GameVersionsApiPath = "/api/v1/game-versions";

    private readonly ITranslationSystemClient _client;

    public GameVersionsLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    public Task<ApiResult<CollectionResponse<GameVersionResponse>>> ListGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.GetApiResultAsync<CollectionResponse<GameVersionResponse>>(
            GameVersionsApiPath,
            cancellationToken);
    }

    public Task<ApiResult<GameVersionResponse>> RegisterGameVersionAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        return _client.PostApiResultAsync<GameVersionResponse>(
            GameVersionsApiPath,
            new RegisterGameVersionRequest(version),
            cancellationToken);
    }

    public Task<ApiResult> DeleteGameVersionAsync(
        GameVersionId id,
        CancellationToken cancellationToken = default)
    {
        return _client.DeleteApiResultAsync(
            string.Create(CultureInfo.InvariantCulture, $"{GameVersionsApiPath}/{id.Value}"),
            cancellationToken);
    }
}
