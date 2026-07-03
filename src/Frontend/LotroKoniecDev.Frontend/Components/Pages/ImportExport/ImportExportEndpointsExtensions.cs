using System.Text;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Maps the artifact download route (M3-07, public since #309). The TMS distribution endpoint serves
/// <c>text/plain</c>, which the typed JSON client cannot stream straight to the browser as a file —
/// so this server route fetches the raw artifact through the same client and re-serves it with a
/// <c>Content-Disposition</c> attachment header named <c>polish.txt</c>. The route is anonymous like
/// the upstream TMS endpoint (players download the file straight off the landing page); the
/// import/export page links to the very same route, so one canonical download URL exists.
/// </summary>
internal static class ImportExportEndpointsExtensions
{
    /// <summary>The public download URL — linked from the landing page and the import/export page.</summary>
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
    /// The download route's request delegate, exposed internally so it can be unit-tested without a
    /// web host: it returns a <see cref="Results.File(byte[],string,string,bool,DateTimeOffset?,Microsoft.Net.Http.Headers.EntityTagHeaderValue)"/>
    /// result on success, or a problem result (the upstream's, or a 502 fallback) on failure.
    /// </summary>
    internal static async Task<IResult> DownloadTranslationFileAsync(
        ImportExportLoader loader,
        CancellationToken cancellationToken)
    {
        ApiResult<string> result = await loader.DownloadTranslationFileAsync(cancellationToken);

        if (result.IsFailure)
        {
            return Results.Problem(result.ProblemDetails ?? new ProblemDetails
            {
                Title = "Nie udało się pobrać pliku tłumaczenia.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        // BOM-less UTF-8: the upstream TMS endpoint serves the artifact as Encoding.UTF8 text and the
        // CLI auto-download (TranslationFileDownloader, M2-20) consumes it via ReadAsStringAsync — the
        // same decode-to-string this route does. Re-encoding without a BOM keeps the served bytes
        // identical to that proven path; the patcher parser int.Parses the first field, so a leading
        // BOM would break it — there must not be one.
        byte[] bytes = Encoding.UTF8.GetBytes(result.Value);

        return Results.File(
            bytes,
            contentType: "text/plain",
            fileDownloadName: ImportExportLoader.DownloadFileName);
    }
}
