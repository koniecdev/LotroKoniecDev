using System.Globalization;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Drives the import/export page's TMS calls through the typed client (M3-07): list game versions to
/// pick the import target (<c>GET /api/v1/game-versions</c>, #103), upload a fresh <c>exported.txt</c>
/// against a chosen version (<c>POST /api/v1/game-versions/{id}/import</c>, #97) and fetch the
/// pre-built <c>polish.txt</c> artifact as raw text (<c>GET /api/v1/translation-files/pl</c>, #102).
/// Kept as a thin injectable seam so the page's data flow is unit-testable end-to-end over a stubbed
/// HTTP handler and so a bUnit render test can drive the page through a substituted loader.
/// </summary>
internal sealed class ImportExportLoader
{
    private const string GameVersionsApiPath = "/api/v1/game-versions";
    private const string FileFieldName = "file";

    /// <summary>The export is the <c>||</c>-format plain-text file (mirrors the API's <c>IFormFile</c> import part).</summary>
    private const string UploadContentType = "text/plain";

    /// <summary>The catalog is Polish-only today (mirrors the API's <c>SupportedLanguages.Polish</c>).</summary>
    private const string Language = "pl";

    private const string TranslationFileApiPath = $"/api/v1/translation-files/{Language}";

    /// <summary>The downloaded artifact's filename, as the patcher's <c>patch</c> command expects it.</summary>
    public const string DownloadFileName = "polish.txt";

    private readonly ITranslationSystemClient _client;

    public ImportExportLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Lists the game versions as the HATEOAS collection envelope (M2-25), <b>keeping its
    /// <c>Links</c></b> (#158): the page reads the collection-level <c>register</c> rel — emitted
    /// admin-only by the API — to gate the import affordance, instead of recomputing the role locally.
    /// </summary>
    public Task<ApiResult<CollectionResponse<GameVersionResponse>>> ListGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.GetApiResultAsync<CollectionResponse<GameVersionResponse>>(
            GameVersionsApiPath,
            cancellationToken);
    }

    public async Task<ApiResult<ImportSummary>> ImportAsync(
        GameVersionId gameVersionId,
        Stream fileStream,
        string fileName,
        bool allowMassRemoval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        using MultipartFormDataContent content = new();
        using StreamContent fileContent = new(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(UploadContentType);
        content.Add(fileContent, FileFieldName, fileName);

        return await _client.SendMultipartApiResultAsync<ImportSummary>(
            HttpMethod.Post,
            ImportUri(gameVersionId, allowMassRemoval),
            content,
            cancellationToken);
    }

    public Task<ApiResult<string>> DownloadTranslationFileAsync(CancellationToken cancellationToken = default)
    {
        return _client.GetTextAsync(TranslationFileApiPath, cancellationToken);
    }

    private static string ImportUri(GameVersionId gameVersionId, bool allowMassRemoval) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{GameVersionsApiPath}/{gameVersionId.Value}/import?allowMassRemoval={allowMassRemoval.ToString().ToLowerInvariant()}");
}
