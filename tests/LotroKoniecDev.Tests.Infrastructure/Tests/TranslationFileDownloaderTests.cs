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
    private const string BaseUrl = "https://tms.example.com";
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
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsModified.ShouldBeTrue();
        result.Value.Content.ShouldBe(Content);
        result.Value.ETag.ShouldBe($"\"{ContentHash}\"");
    }

    [Fact]
    public async Task FetchAsync_BodyNotMatchingTheETagHash_ShouldRejectTheDownload()
    {
        // Arrange — the served bytes do not hash to the advertised ETag (corruption or tampering).
        using HttpResponseMessage response = OkResponse("tampered body", $"\"{ContentHash}\"");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseWithoutAnETag_ShouldRejectTheDownload()
    {
        // Arrange — no validator means no way to verify the body: fail closed.
        using HttpResponseMessage response = OkResponse(Content, eTag: null);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseWithAWeakETag_ShouldRejectTheDownload()
    {
        // Arrange — a weak validator cannot guarantee byte equality, so it is as unverifiable as none.
        using HttpResponseMessage response = OkResponse(Content, eTag: null);
        response.Headers.ETag = new EntityTagHeaderValue($"\"{ContentHash}\"", isWeak: true);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.IntegrityCheckFailedCode);
    }

    [Fact]
    public async Task FetchAsync_ResponseDeclaringABodyOverTheSizeCap_ShouldRejectTheDownload()
    {
        // Arrange — a Content-Length above the cap (AUDIT-SEC-04 / #394) must be refused up front,
        // before any body byte is read.
        using HttpResponseMessage response = OkResponse(Content, $"\"{ContentHash}\"");
        response.Content.Headers.ContentLength = TranslationFileDownloader.MaxResponseContentBytes + 1;
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchAsync_UndeclaredBodyStreamingPastTheSizeCap_ShouldRejectTheDownload()
    {
        // Arrange — no Content-Length (chunked-style), the body itself overruns the cap while
        // streaming: the buffer limit must cut it off instead of growing without bound.
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new UndeclaredLengthContent(TranslationFileDownloader.MaxResponseContentBytes + 1)
        };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchAsync_BodyThatStallsPastTheClientTimeout_ShouldFailAsTimedOutInsteadOfHanging()
    {
        // Arrange — ResponseHeadersRead moves the body read out of HttpClient.Timeout's scope;
        // the downloader must re-apply the timeout itself or a stalling server hangs the launch.
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StallingContent() };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        httpClient.Timeout = TimeSpan.FromMilliseconds(250);
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Fact]
    public async Task FetchAsync_NotModifiedResponse_ShouldReportTheCachedCopyCurrentWithoutAnyCheck()
    {
        // Arrange — a 304 carries no body, so there is nothing to verify.
        using HttpResponseMessage response = new(HttpStatusCode.NotModified);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationFileDownloader sut = new(httpClient);

        // Act
        Result<TranslationFileFetchResult> result = await sut.FetchAsync(BaseUrl, "\"cached-etag\"", CancellationToken.None);

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
