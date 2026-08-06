using System.Net;
using System.Text;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// Drives the download route's request delegate directly (no web host): the TMS distribution endpoint
/// serves <c>text/plain</c>, and this route must re-serve it as a <c>polish.txt</c> file attachment on
/// success, or surface a problem (the upstream's, or a 502 fallback) on failure.
/// </summary>
public sealed class ImportExportEndpointsExtensionsTests
{
    private const string BaseUrl = "https://localhost:5002/";

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_ReturnsThePolishTxtFileWithTheArtifactBytes()
    {
        const string body = "# polish.txt\n620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1";
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, body);

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        file.FileDownloadName.ShouldBe(ImportExportLoader.DownloadFileName);
        file.ContentType.ShouldBe("text/plain");
        file.FileContents.ToArray().ShouldBe(Encoding.UTF8.GetBytes(body));
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_DoesNotPrependAByteOrderMark()
    {
        // The patcher parser int.Parses the first field, so a leading BOM would break it.
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, "620756992||1001||Witaj||NULL||NULL||1");

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, CancellationToken.None);

        FileContentHttpResult file = result.ShouldBeOfType<FileContentHttpResult>();
        byte[] preamble = Encoding.UTF8.GetPreamble();
        file.FileContents.Length.ShouldBeGreaterThan(preamble.Length);
        file.FileContents[..preamble.Length].ToArray().ShouldNotBe(preamble);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenUpstreamReturnsProblem_SurfacesThatProblem()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.NotFound,
            """{ "title": "Brak pliku tłumaczenia", "status": 404 }""");

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenUpstreamReturnsAnEnglishProblem_RewritesItInPolish()
    {
        // The browser shows this body verbatim — it is a download route, not a rendered page — so the
        // same errorCode→Polish rule applies here as on a page (#548 / ADR-0044).
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.NotFound,
            """
            {
              "title": "Not Found",
              "status": 404,
              "detail": "No translation file has been built for 'pl' yet.",
              "errorCode": "TranslationFiles.NotFound"
            }
            """);

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
        problem.ProblemDetails.Title.ShouldBe(
            "Plik z tłumaczeniami nie został jeszcze zbudowany. Zatwierdź przynajmniej jedno tłumaczenie i spróbuj ponownie.");
        problem.ProblemDetails.Extensions[ApiProblemCopy.TechnicalDetailExtensionKey]
            .ShouldBe("TranslationFiles.NotFound — No translation file has been built for 'pl' yet.");
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTransportFails_FallsBackToABadGatewayProblem()
    {
        // A transport failure yields a synthesized ProblemDetails (503) from the HTTP seam; the route
        // passes it through. The 502 fallback only fires for the (defensive) null-ProblemDetails case.
        ImportExportLoader loader = new(
            StubDiscoveryCache.AdvertisingGet(Rels.TranslationFile),
            CreateClient(StubHttpMessageHandler.Throw(new HttpRequestException("connection refused"))));

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private static ImportExportLoader CreateLoader(HttpStatusCode statusCode, string body) =>
        new(
            StubDiscoveryCache.AdvertisingGet(Rels.TranslationFile),
            CreateClient(StubHttpMessageHandler.RespondWith(statusCode, body)));

    private static ITranslationSystemClient CreateClient(StubHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        return new TranslationSystemClient(httpClient);
    }
}
