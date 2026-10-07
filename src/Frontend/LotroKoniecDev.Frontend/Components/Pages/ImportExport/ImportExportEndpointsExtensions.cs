using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Maps the download route for the translation file (M3-07, public since #309). The TMS endpoint serves
/// <c>text/plain</c>, which the typed JSON client cannot pass straight to the browser as a file. So this
/// server route fetches the file through the same client and sends it again with a
/// <c>Content-Disposition</c> attachment header named <c>polish.txt</c>.
/// The route is open to anyone, like the TMS endpoint behind it, because players download the file
/// straight from the landing page. The import/export page links to the same route, so there is one
/// download URL.
/// </summary>
internal static class ImportExportEndpointsExtensions
{
    /// <summary>The public download URL, linked from the landing page and the import/export page.</summary>
    internal const string DownloadPath = "/download/polish.txt";

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapImportExportEndpoints()
        {
            endpoints.MapGet(DownloadPath, DownloadTranslationFileAsync)
                .AllowAnonymous();

            return endpoints;
        }
    }

    /// <summary>
    /// The route's handler, internal so a unit test can call it without a web host. On success it returns
    /// a <see cref="Results.Stream(Stream,string,string,DateTimeOffset?,Microsoft.Net.Http.Headers.EntityTagHeaderValue,bool)"/>
    /// result, and on failure a problem result, either the one from the API or a 502 of our own.
    /// </summary>
    internal static async Task<IResult> DownloadTranslationFileAsync(
        ImportExportLoader loader,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ApiResult<ApiBodyStream> result = await loader.DownloadTranslationFileAsync(cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(ApiProblemCopy.Localize(
                loggerFactory,
                result.ProblemDetails,
                "Nie udało się pobrać pliku tłumaczenia.",
                StatusCodes.Status502BadGateway));
        }

        // The bytes go to the browser exactly as the TMS sent them, without being held here: the route
        // is public, and a full copy per request would let a crowd of players fill this container's
        // memory (PERF-09, #715). Those bytes are what the TMS hashes into its ETag, UTF-8 with no BOM.
        // The stream cannot report its own length, so the TMS's length is passed on by hand. It gives
        // the browser its progress bar, and Kestrel aborts a body that ends short of it.
        httpContext.Response.ContentLength = result.Value.Length;
        return Results.Stream(
            result.Value.Content,
            contentType: "text/plain",
            fileDownloadName: ImportExportLoader.DownloadFileName);
    }
}
