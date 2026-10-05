using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Tests.Infrastructure.Shared;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the fetcher's response-size limit (AUDIT-SEC-04, #394) over an in-memory stub handler, with
/// no network. The forum page comes from outside, so a body over the limit has to come back as a failure
/// instead of using up all our memory.
/// </summary>
public sealed class ForumPageFetcherTests
{
    private const string PageContent = "<html>Update 48.0 Release Notes</html>";
    private const string PolishPageContent = "<html>Aktualizacja 48.0: zażółć gęślą jaźń</html>";

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

    [Theory]
    [InlineData("utf8")]
    [InlineData("bogus")]
    [InlineData("\"utf8\"")]
    [InlineData("utf-7")]
    public async Task FetchReleaseNotesPageAsync_PageNamingACharsetDotNetCannotUse_ShouldReadItAsUtf8(string charset)
    {
        // Arrange: #972. The forum is a third-party site, so the header is outside our control. A name
        // .NET does not know, or refuses like "utf-7", must not stop the launch.
        using HttpResponseMessage response = BytesResponse(Encoding.UTF8.GetBytes(PolishPageContent), charset);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(PolishPageContent);
    }

    [Fact]
    public async Task FetchReleaseNotesPageAsync_PageNamingAnUnknownCharsetWithAUtf8ByteOrderMark_ShouldDropTheMark()
    {
        // Arrange
        using HttpResponseMessage response = BytesResponse(
            [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(PolishPageContent)], "utf8");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(PolishPageContent);
    }

    [Theory]
    [InlineData("iso-8859-1")]
    [InlineData("\"iso-8859-1\"")]
    public async Task FetchReleaseNotesPageAsync_PageNamingAKnownNonUtf8Charset_ShouldStillDecodeItByThatCharset(string charset)
    {
        // Arrange: the page is HTML, so a real charset still counts. In Latin-1 "é" is the single byte
        // 0xE9, which read as UTF-8 would turn into U+FFFD.
        const string page = "<html>Café</html>";
        using HttpResponseMessage response = BytesResponse(Encoding.Latin1.GetBytes(page), charset);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        ForumPageFetcher sut = new(httpClient);

        // Act
        Result<string> result = await sut.FetchReleaseNotesPageAsync();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(page);
    }

    private static HttpResponseMessage BytesResponse(byte[] body, string charset) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset } }
            }
        };

    private static HttpResponseMessage OkResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        };
}
