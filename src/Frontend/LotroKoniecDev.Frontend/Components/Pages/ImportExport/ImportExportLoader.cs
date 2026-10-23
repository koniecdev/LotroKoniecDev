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
/// The import/export page's calls to the TMS. The import follows the <c>import</c> rel of the version it
/// goes into, which the API sends only to an admin and never for a superseded version. A thin class, so a
/// bUnit test can drive the page through a substituted loader.
/// </summary>
internal sealed class ImportExportLoader
{
    private const string FileFieldName = "file";

    /// <summary>The caller's choice for this one upload, which the server cannot put in the link.</summary>
    private const string AllowMassRemovalParameter = "allowMassRemoval";

    private const string UploadContentType = "text/plain";

    /// <summary>The name the patcher's <c>patch</c> command expects.</summary>
    public const string DownloadFileName = "polish.txt";

    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public ImportExportLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
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

    /// <summary>
    /// <paramref name="importHref"/> comes from the server and already carries the version id.
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

    public async Task<ApiResult<ApiBodyStream>> DownloadTranslationFileAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.TranslationFile,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<ApiBodyStream>(href.ProblemDetails!);
        }

        return await _client.GetBodyStreamAsync(href.Value, cancellationToken);
    }
}
