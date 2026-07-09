using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Network;

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

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
