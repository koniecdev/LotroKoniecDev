using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.WebUtilities;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Drives the import/export page's TMS calls through the typed client (M3-07). Two entry points come
/// from the service document (#610): the <c>game-versions</c> rel to pick the import target (#103) and
/// the <c>translation-file</c> rel for the pre-built <c>polish.txt</c> artifact (#102, anonymous). The
/// import itself (#97) is keyed by the version it lands against, so it follows the <c>import</c> rel the
/// chosen version advertises — emitted admin-only, and never for a superseded version. Kept as a thin
/// injectable seam so the page's data flow is unit-testable end-to-end over a stubbed HTTP handler and
/// so a bUnit render test can drive the page through a substituted loader.
/// </summary>
internal sealed class ImportExportLoader
{
    private const string FileFieldName = "file";

    /// <summary>The caller's per-upload override; the server cannot know it when it emits the link.</summary>
    private const string AllowMassRemovalParameter = "allowMassRemoval";

    /// <summary>The export is the <c>||</c>-format plain-text file (mirrors the API's <c>IFormFile</c> import part).</summary>
    private const string UploadContentType = "text/plain";

    /// <summary>The downloaded artifact's filename, as the patcher's <c>patch</c> command expects it.</summary>
    public const string DownloadFileName = "polish.txt";

    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public ImportExportLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    /// <summary>
    /// Lists the game versions as the HATEOAS collection envelope (M2-25), <b>keeping its
    /// <c>Links</c></b> (#158): the page reads the collection-level <c>register</c> rel — emitted
    /// admin-only by the API — to gate the import panel, and each item's <c>import</c> rel to address
    /// the upload, instead of recomputing the role locally.
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
    /// Uploads an <c>exported.txt</c> against the version whose <c>import</c> link is
    /// <paramref name="importHref"/> — the server-advertised URI, which already carries the version id.
    /// Only <c>allowMassRemoval</c> is appended, because it is the caller's choice for this one upload
    /// and nothing the server could have known when it emitted the link.
    /// </summary>
    public async Task<ApiResult<ImportSummary>> ImportAsync(
        string importHref,
        Stream fileStream,
        string fileName,
        bool allowMassRemoval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importHref);
        ArgumentNullException.ThrowIfNull(fileStream);

        using MultipartFormDataContent content = new();
        using StreamContent fileContent = new(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(UploadContentType);
        content.Add(fileContent, FileFieldName, fileName);

        return await _client.SendMultipartApiResultAsync<ImportSummary>(
            HttpMethod.Post,
            QueryHelpers.AddQueryString(importHref, AllowMassRemovalParameter, allowMassRemoval ? "true" : "false"),
            content,
            cancellationToken);
    }

    public async Task<ApiResult<string>> DownloadTranslationFileAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.TranslationFile,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<string>(href.ProblemDetails!);
        }

        return await _client.GetTextAsync(href.Value, cancellationToken);
    }
}
