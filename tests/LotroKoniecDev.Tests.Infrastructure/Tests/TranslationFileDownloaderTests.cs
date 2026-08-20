using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Tests.Infrastructure.Shared;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the downloader's integrity enforcement point (AUDIT-SEC-01 / #391) over an in-memory
/// stub handler — no real network. The pure hash comparison itself is covered in
/// <c>Tests.Unit/TranslationFileContentIntegrityTests</c>; these tests prove the downloader actually
/// applies it before handing content to the sync.
/// </summary>
public sealed class TranslationFileDownloaderTests
{
    private static readonly Uri Endpoint = new("https://tms.example.com/api/v1/translation-files/pl");
    private const string Content = "polish content";

    /// <summary>Independently computed (shell <c>shasum -a 256</c>), never via the code under test.</summary>
    private const string ContentHash = "579BDE6E87308282DEA0FCB1A3E8AF668BF6F558CC4545457C696EFB75F7FD18";

    [Fact]
    public async Task FetchAsync_BodyMatchingTheETagHash_ShouldReturnModifiedContent()
    {
        // Arrange
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsModified.ShouldBeTrue();
        result.Value.Content.ShouldBe(Content);
        result.Value.ETag.ShouldBe($"\"{ContentHash}\"");
    }

    [Fact]
    public async Task FetchAsync_BodyNotMatchingTheETagHash_ShouldRejectTheDownload()
    {
        // Arrange: the served bytes do not hash to the advertised ETag (corruption or tampering).
        using HttpResponseMessage response = OkResponse("tampered body", $"\"{ContentHash}\"");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseWithoutAnETag_ShouldRejectTheDownload()
    {
        // Arrange: no validator means no way to verify the body: fail closed.
        using HttpResponseMessage response = OkResponse(Content, eTag: null);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseWithAWeakETag_ShouldRejectTheDownload()
    {
        // Arrange: a weak validator cannot guarantee byte equality, so it is as unverifiable as none.
        using HttpResponseMessage response = OkResponse(Content, eTag: null);
        response.Headers.ETag = new EntityTagHeaderValue($"\"{ContentHash}\"", isWeak: true);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseDeclaringABodyOverTheSizeCap_ShouldRejectTheDownload()
    {
        // Arrange: a Content-Length above the cap (AUDIT-SEC-04 / #394) must be refused up front,
        // before any body byte is read.
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        response.Content.Headers.ContentLength = TranslationFileDownloader.MaxResponseContentBytes + 1;
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchAsync_UndeclaredBodyStreamingPastTheSizeCap_ShouldRejectTheDownload()
    {
        // Arrange: no Content-Length (chunked-style), the body itself overruns the cap while
        // streaming: the buffer limit must cut it off instead of growing without bound.
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new UndeclaredLengthContent(TranslationFileDownloader.MaxResponseContentBytes + 1)
        };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchAsync_BodyThatStallsPastTheClientTimeout_ShouldFailAsTimedOutInsteadOfHanging()
    {
        // Arrange: ResponseHeadersRead moves the body read out of HttpClient.Timeout's scope;
        // the downloader must re-apply the timeout itself or a stalling server hangs the launch.
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StallingContent() };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        httpClient.Timeout = TimeSpan.FromMilliseconds(250);
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Fact]
    public async Task FetchAsync_WellFormedCachedETag_ShouldSendItAsTheIfNoneMatchHeader()
    {
        // Arrange: the sidecar value parses as an ETag, so the conditional header goes out
        // through the typed API (AUDIT-SEC-07 / #397).
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        StubHttpMessageHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, "\"cached-etag\"", CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.Headers.IfNoneMatch.Single().Tag.ShouldBe("\"cached-etag\"");
    }

    [Theory]
    [InlineData("\"etag\"\r\nX-Injected: attack")]
    [InlineData("etag-without-quotes")]
    public async Task FetchAsync_MalformedCachedETag_ShouldFetchTheFullFileWithoutTheConditionalHeader(string malformedETag)
    {
        // Arrange: a sidecar value that no longer parses as an ETag (tampering or corruption)
        // must never reach the wire; the fetch degrades to a full download (AUDIT-SEC-07 / #397).
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        StubHttpMessageHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, malformedETag, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsModified.ShouldBeTrue();
        result.Value.Content.ShouldBe(Content);
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.Headers.IfNoneMatch.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_ShouldRequestTheResolvedEndpointVerbatim()
    {
        // Arrange: the endpoint arrives already resolved from discovery, so the downloader appends
        // nothing to it: there is no route left in the patcher source to append (#611).
        Uri movedEndpoint = new("https://tms.example.com/downloads/pl.txt");
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        StubHttpMessageHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(movedEndpoint, null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.RequestUri.ShouldBe(movedEndpoint);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task FetchAsync_RedirectResponse_ShouldBeRejectedRatherThanFollowedOffTheOrigin(HttpStatusCode status)
    {
        // Arrange: the resolved endpoint was validated to be on the configured origin, and a 302 is
        // exactly how a hostile server would walk around that check: the body (and the ETag hashing
        // it) would then come from the redirect target, so the integrity check would confirm the
        // wrong file. The TMS client is registered with redirects OFF (#611) and a 3xx is a failure.
        using HttpResponseMessage response = new(status);
        response.Headers.Location = new Uri("https://evil.example.com/api/v1/translation-files/pl");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Fact]
    public async Task FetchAsync_NotModifiedResponse_ShouldReportTheCachedCopyCurrentWithoutAnyCheck()
    {
        // Arrange: a 304 carries no body, so there is nothing to verify.
        using HttpResponseMessage response = new(HttpStatusCode.NotModified);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(Endpoint, "\"cached-etag\"", CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsModified.ShouldBeFalse();
    }

    private static HttpResponseMessage OkResponse(string body, string? eTag)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };

        if (eTag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(eTag);
        }

        return response;
    }
}
