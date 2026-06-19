using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.ContentNegotiation;

/// <summary>
/// The HATEOAS-bearing list/detail endpoints negotiate a single payload between plain JSON and the
/// vendor representation. The existing aggregate HATEOAS suites assert the link sets; this pins the
/// negotiation contract the cache layer depends on — the served media type follows the <c>Accept</c>
/// header, and <c>Vary: Accept</c> is emitted for <em>both</em> representations (RFC 9110 §12.5.5).
/// </summary>
[Collection("TranslationApi")]
public sealed class ContentNegotiationTests
{
    private const string ListRoute = "/api/v1/translations";

    // The vendor media type is the public wire contract (LotroKoniecDev.Hateoas MediaTypes.HateoasJson).
    private const string HateoasMediaType = "application/vnd.dev-lotrokoniecdev.hateoas.json";
    private const string JsonMediaType = "application/json";

    private readonly TranslationSystemApiFactory _factory;

    public ContentNegotiationTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(JsonMediaType, JsonMediaType)]
    [InlineData(HateoasMediaType, HateoasMediaType)]
    public async Task ListTranslations_ShouldServeNegotiatedMediaTypeAndVaryByAccept(string accept, string expectedMediaType)
    {
        // Arrange
        using HttpClient client = TranslatorClient();
        using HttpRequestMessage request = new(HttpMethod.Get, ListRoute);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(expectedMediaType);
        // Vary: Accept is appended regardless of the negotiated representation so shared caches treat
        // the two media types as distinct entities.
        response.Headers.Vary.ShouldContain("Accept");
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }
}
