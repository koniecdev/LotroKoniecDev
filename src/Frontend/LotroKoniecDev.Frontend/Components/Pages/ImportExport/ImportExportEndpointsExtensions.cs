using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// The translation file download. A server route sends the TMS file on as a <c>polish.txt</c> attachment.
/// It is public, like the TMS endpoint behind it, because players download the file from the landing page
/// (#309).
/// </summary>
internal static class ImportExportEndpointsExtensions
{
    /// <summary>Linked from the landing page and the import/export page.</summary>
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

    /// <summary>Internal so a unit test can call it without a web host.</summary>
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

        // Streamed, not held: the route is public, and a full copy per request would let a crowd of
        // players fill this container's memory (#715). The bytes stay exactly as the TMS hashed them into
        // its ETag. The TMS's length is passed on by hand for the browser's progress bar; Kestrel aborts a
        // body that ends short of it.
        httpContext.Response.ContentLength = result.Value.Length;
        return Results.Stream(
            result.Value.Content,
            contentType: "text/plain",
            fileDownloadName: ImportExportLoader.DownloadFileName);
    }
}
