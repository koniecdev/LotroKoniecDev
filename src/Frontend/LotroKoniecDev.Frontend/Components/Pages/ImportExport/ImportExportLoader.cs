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
/// Makes the import/export page's calls to the TMS through the typed client (M3-07). Two entry points
/// come from the service document (#610): the <c>game-versions</c> rel to choose where an import goes
/// (#103), and the <c>translation-file</c> rel for the ready-made <c>polish.txt</c> (#102, open to
/// anyone).
/// The import itself (#97) is tied to the version it goes into, so it follows the <c>import</c> rel that
/// version offers, which the API sends only to an admin and never for a superseded version.
/// It stays a thin injectable class, so the page's data flow can be unit-tested end to end against a
/// stubbed HTTP handler and a bUnit render test can drive the page through a substituted loader.
/// </summary>
internal sealed class ImportExportLoader
{
    private const string FileFieldName = "file";

    /// <summary>The caller's choice for this one upload. The server cannot know it when it builds the link.</summary>
    private const string AllowMassRemovalParameter = "allowMassRemoval";

    /// <summary>The export is the plain-text <c>||</c> file, the same shape the API's import part expects.</summary>
    private const string UploadContentType = "text/plain";

    /// <summary>The name of the downloaded file, as the patcher's <c>patch</c> command expects it.</summary>
    public const string DownloadFileName = "polish.txt";

    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public ImportExportLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    /// <summary>
    /// Lists the game versions and <b>keeps the <c>Links</c></b> the API sent (M2-25, #158). The page
    /// uses the collection's <c>register</c> rel, which the API sends only to an admin, to decide whether
    /// to show the import panel, and each item's <c>import</c> rel as the upload URL, instead of working
    /// the role out itself.
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
    /// Uploads an <c>exported.txt</c> to the version whose <c>import</c> link is
    /// <paramref name="importHref"/>. That URI comes from the server and already carries the version id.
    /// Only <c>allowMassRemoval</c> is added, because it is the caller's choice for this one upload and
    /// something the server could not have known when it built the link.
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
