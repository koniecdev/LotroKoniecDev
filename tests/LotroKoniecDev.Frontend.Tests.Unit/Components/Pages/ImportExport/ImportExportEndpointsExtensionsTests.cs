using System.Net;
using System.Text;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

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

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, CancellationToken.None);

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

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, CancellationToken.None);

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

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTransportFails_FallsBackToABadGatewayProblem()
    {
        // A transport failure yields a synthesized ProblemDetails (503) from the HTTP seam; the route
        // passes it through. The 502 fallback only fires for the (defensive) null-ProblemDetails case.
        ImportExportLoader loader = new(CreateClient(
            StubHttpMessageHandler.Throw(new HttpRequestException("connection refused"))));

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private static ImportExportLoader CreateLoader(HttpStatusCode statusCode, string body) =>
        new(CreateClient(StubHttpMessageHandler.RespondWith(statusCode, body)));

    private static ITranslationSystemClient CreateClient(StubHttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        return new TranslationSystemClient(httpClient);
    }
}
