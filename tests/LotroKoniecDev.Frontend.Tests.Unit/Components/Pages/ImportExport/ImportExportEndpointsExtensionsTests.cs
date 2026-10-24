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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.ImportExport;

/// <summary>
/// Calls the download route's handler directly, with no web host.
/// </summary>
public sealed class ImportExportEndpointsExtensionsTests
{
    private const string BaseUrl = "https://localhost:5002/";

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_StreamsThePolishTxtFileWithTheArtifactBytes()
    {
        // A stream, not a byte array: a full copy per request on a public route would fill memory (#715).
        const string body = "# polish.txt\n620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1";
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, body);

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        FileStreamHttpResult file = result.ShouldBeOfType<FileStreamHttpResult>();
        file.FileDownloadName.ShouldBe(ImportExportLoader.DownloadFileName);
        file.ContentType.ShouldBe("text/plain");
        (await ReadAllBytesAsync(file.FileStream)).ShouldBe(Encoding.UTF8.GetBytes(body));
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_SendsTheBytesWithTheirLength()
    {
        // A body off the connection cannot report its length, so the route passes the TMS's on. A
        // seekable stub would hide that.
        const string body = "620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ImportExportLoader loader = new(
            StubDiscoveryCache.AdvertisingGet(Rels.TranslationFile),
            CreateClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, () => new StreamContent(new NonSeekableStream(bytes))
            {
                Headers = { ContentLength = bytes.Length }
            })));
        DefaultHttpContext httpContext = new()
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        httpContext.Response.ContentLength.ShouldBe(bytes.Length);
        httpContext.Response.Headers.ContentDisposition.ToString().ShouldContain(ImportExportLoader.DownloadFileName);
        ((MemoryStream)httpContext.Response.Body).ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_OnSuccess_DoesNotPrependAByteOrderMark()
    {
        // The patcher parser int.Parses the first field, so a leading BOM would break it.
        ImportExportLoader loader = CreateLoader(HttpStatusCode.OK, "620756992||1001||Witaj||NULL||NULL||1");

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        FileStreamHttpResult file = result.ShouldBeOfType<FileStreamHttpResult>();
        byte[] bytes = await ReadAllBytesAsync(file.FileStream);
        byte[] preamble = Encoding.UTF8.GetPreamble();
        bytes.Length.ShouldBeGreaterThan(preamble.Length);
        bytes[..preamble.Length].ShouldNotBe(preamble);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenUpstreamReturnsProblem_SurfacesThatProblem()
    {
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.NotFound,
            """{ "title": "Brak pliku tłumaczenia", "status": 404 }""");

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenUpstreamReturnsAnEnglishProblem_RewritesItInPolish()
    {
        // The browser shows a download route's body as it is, so the errorCode decides the Polish text
        // (ADR-0044).
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

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(404);
        problem.ProblemDetails.Title.ShouldBe(
            "Plik z tłumaczeniami nie został jeszcze zbudowany. Zatwierdź przynajmniej jedno tłumaczenie i spróbuj ponownie.");
        // The English only restates the Polish, so it is dropped (#703).
        problem.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.TechnicalDetailExtensionKey);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTransportFails_FallsBackToABadGatewayProblem()
    {
        // The HTTP layer's 503 passes through; the 502 fallback is only for a null problem.
        ImportExportLoader loader = new(
            StubDiscoveryCache.AdvertisingGet(Rels.TranslationFile),
            CreateClient(StubHttpMessageHandler.Throw(new HttpRequestException("connection refused"))));

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task DownloadTranslationFileAsync_WhenTheProxyAnswersForAStoppedUpstream_ServesThePolishStatusCopy()
    {
        // #637: tms-api down, so Caddy answers 502 with its own HTML.
        ImportExportLoader loader = CreateLoader(
            HttpStatusCode.BadGateway,
            "<html><head><title>502 Bad Gateway</title></head><body></body></html>");

        IResult result = await ImportExportEndpointsExtensions.DownloadTranslationFileAsync(loader, NullLoggerFactory.Instance, new DefaultHttpContext(), CancellationToken.None);

        ProblemHttpResult problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.ProblemDetails.Status.ShouldBe(StatusCodes.Status502BadGateway);
        problem.ProblemDetails.Title.ShouldBe("Usługa jest chwilowo niedostępna. Spróbuj ponownie za chwilę.");
        problem.ProblemDetails.Extensions.ShouldNotContainKey(ApiProblemCopy.TechnicalDetailExtensionKey);
    }

    /// <summary>Like a body off the network: forward only, with no length of its own.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] bytes)
        {
            _inner = new MemoryStream(bytes, writable: false);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        await using Stream owned = stream;
        using MemoryStream copy = new();
        await owned.CopyToAsync(copy);
        return copy.ToArray();
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
