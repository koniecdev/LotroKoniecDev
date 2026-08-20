using System.Net;
using System.Text;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Tests.Infrastructure.Shared;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the fetcher's response-size cap (AUDIT-SEC-04 / #394) over an in-memory stub
/// handler — no real network. The forum page crosses a remote trust boundary, so an over-cap
/// body must surface as a failure result instead of exhausting process memory.
/// </summary>
public sealed class ForumPageFetcherTests
{
    private const string PageContent = "<html>Update 48.0 Release Notes</html>";

    [Fact]
    public async Task FetchReleaseNotesPageAsync_PageWithinTheSizeCap_ShouldReturnItsContent()
    {
        // Arrange
        using HttpResponseMessage response = OkResponse(PageContent);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(PageContent);
    }

    [Fact]
    public async Task FetchReleaseNotesPageAsync_ResponseDeclaringABodyOverTheSizeCap_ShouldReturnFailure()
    {
        // Arrange: a Content-Length above the cap must be refused before any body byte is read.
        using HttpResponseMessage response = OkResponse(PageContent);
        response.Content.Headers.ContentLength = ForumPageFetcher.MaxResponseContentBytes + 1;
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.GameUpdateCheck.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchReleaseNotesPageAsync_UndeclaredBodyStreamingPastTheSizeCap_ShouldReturnFailure()
    {
        // Arrange: no Content-Length (chunked-style), the body itself overruns the cap while
        // streaming: the buffer limit must cut it off instead of growing without bound.
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new UndeclaredLengthContent(ForumPageFetcher.MaxResponseContentBytes + 1)
        };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.GameUpdateCheck.ResponseTooLargeCode);
    }

    [Fact]
    public async Task FetchReleaseNotesPageAsync_BodyThatStallsPastTheClientTimeout_ShouldFailAsTimedOutInsteadOfHanging()
    {
        // Arrange: ResponseHeadersRead moves the body read out of HttpClient.Timeout's scope;
        // the fetcher must re-apply the timeout itself or a stalling server hangs preflight.
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StallingContent() };
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        httpClient.Timeout = TimeSpan.FromMilliseconds(250);
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.NetworkError");
    }

    [Fact]
    public async Task FetchReleaseNotesPageAsync_ErrorStatusCode_ShouldReturnNetworkErrorFailure()
    {
        // Arrange
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.NetworkError");
    }

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };
}
