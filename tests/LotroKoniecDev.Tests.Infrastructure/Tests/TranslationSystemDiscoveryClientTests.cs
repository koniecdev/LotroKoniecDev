using System.Net;
using System.Text;
using System.Text.Json;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Tests.Infrastructure.Shared;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Drives the CLI's discovery adapter over an in-memory stub handler, with no network. It proves the
/// details of the request that the resolver's own tests cannot see: the root is requested at the
/// configured base URL, our vendor media type is asked for, without which the server sends a document
/// with no links, and a body that is not the service document becomes a failure instead of throwing.
/// </summary>
public sealed class TranslationSystemDiscoveryClientTests
{
    private const string BaseUrl = "https://tms.example.com";

    private const string DownloadHref = "https://tms.example.com/api/v1/translation-files/pl";

    private const string ServiceDocument = """
        {
          "name": "LotroKoniecDev.TranslationSystem",
          "links": [
            { "href": "https://tms.example.com/", "rel": "self", "method": "GET" },
            { "href": "https://tms.example.com/api/v1/translation-files/pl", "rel": "translation-file", "method": "GET" }
          ]
        }
        """;

    [Fact]
    public async Task FetchLinksAsync_ServiceDocument_ShouldReturnEveryAdvertisedLink()
    {
        // Arrange
        using HttpResponseMessage response = JsonResponse(ServiceDocument);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
        result.Value.ShouldContain(link =>
            link.Rel == "translation-file"
            && link.Href == "https://tms.example.com/api/v1/translation-files/pl"
            && link.Method == "GET");
    }

    [Fact]
    public async Task FetchLinksAsync_ShouldRequestTheRootAndNegotiateTheHateoasMediaType()
    {
        // Arrange: links are opt-in: a request that does not accept the vendor media type gets the
        // payload with no links at all, which would silently strip the CLI of every entry point.
        using HttpResponseMessage response = JsonResponse(ServiceDocument);
        StubHttpMessageHandler handler = new(response);
        using HttpClient httpClient = new(handler);
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync($"{BaseUrl}/", CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.RequestUri.ShouldBe(new Uri($"{BaseUrl}/"));
        handler.LastRequest.Headers.Accept.ShouldContain(header => header.MediaType == MediaTypes.HateoasJson);
    }

    [Fact]
    public async Task FetchLinksAsync_ServerError_ShouldReturnANetworkFailure()
    {
        // Arrange
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("{\"name\":\"LotroKoniecDev.TranslationSystem\"}")]
    public async Task FetchLinksAsync_BodyThatIsNotAServiceDocument_ShouldFailInsteadOfThrowing(string body)
    {
        // Arrange: a proxy error page or a link-less payload is a failed discovery, not a crash.
        using HttpResponseMessage response = JsonResponse(body);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Fact]
    public async Task FetchLinksAsync_ABodyBuiltFromTheRealLinkDto_ShouldStillParse()
    {
        // Arrange: the CLI re-types the link envelope (it must not link the TMS side), so this is a
        // wire contract with two independent definitions. Serializing the SERVER's own LinkDto with
        // the same web defaults ASP.NET uses is the drift guard: rename or re-case a member there and
        // this fails, instead of every installed CLI quietly seeing an empty link set.
        string body = JsonSerializer.Serialize(
            new { name = "LotroKoniecDev.TranslationSystem", links = new[] { new LinkDto(DownloadHref, "translation-file", "GET") } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using HttpResponseMessage response = JsonResponse(body);
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldHaveSingleItem();
        result.Value[0].ShouldBe(new DiscoveredLink(DownloadHref, "translation-file", "GET"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task FetchLinksAsync_RedirectResponse_ShouldNotBeTreatedAsASuccessfulDocument(HttpStatusCode status)
    {
        // Arrange: the TMS client is registered with redirects OFF (#611): a 3xx that would carry the
        // CLI to another origin must surface as a failed discovery, never as a document to trust.
        using HttpResponseMessage response = new(status);
        response.Headers.Location = new Uri("https://evil.example.com/api/v1/translation-files/pl");
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.NetworkError");
    }

    [Fact]
    public async Task FetchLinksAsync_ResponseLargerThanTheCap_ShouldBeRejectedBeforeItIsRead()
    {
        // Arrange: a hostile or misbehaving server must not be able to exhaust process memory
        // (AUDIT-SEC-04 / #394); the service document is a few KB of links.
        using HttpResponseMessage response = JsonResponse(ServiceDocument);
        response.Content.Headers.ContentLength = TranslationSystemDiscoveryClient.MaxResponseContentBytes + 1;
        using HttpClient httpClient = new(new StubHttpMessageHandler(response));
        TranslationSystemDiscoveryClient sut = new(httpClient);

        // Act
        Result<IReadOnlyList<DiscoveredLink>> result = await sut.FetchLinksAsync(BaseUrl, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.ResponseTooLargeCode);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypes.HateoasJson)
        };
}
