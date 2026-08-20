using System.Text;
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
    /// a <see cref="Results.File(byte[],string,string,bool,DateTimeOffset?,Microsoft.Net.Http.Headers.EntityTagHeaderValue)"/>
    /// result, and on failure a problem result, either the one from the API or a 502 of our own.
    /// </summary>
    internal static async Task<IResult> DownloadTranslationFileAsync(
        ImportExportLoader loader,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ApiResult<string> result = await loader.DownloadTranslationFileAsync(cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(ApiProblemCopy.Localize(
                loggerFactory,
                result.ProblemDetails,
                "Nie udało się pobrać pliku tłumaczenia.",
                StatusCodes.Status502BadGateway));
        }

        // UTF-8 without a BOM. The TMS endpoint serves the file as Encoding.UTF8 text, and the CLI
        // download (TranslationFileDownloader, M2-20) reads it with ReadAsStringAsync, the same way this
        // route does. Encoding it again without a BOM keeps the bytes identical to that proven path.
        // The patcher parses the first field as a number, so a BOM at the start would break it.
        byte[] bytes = Encoding.UTF8.GetBytes(result.Value);

        return Results.File(
            bytes,
            contentType: "text/plain",
            fileDownloadName: ImportExportLoader.DownloadFileName);
    }
}
