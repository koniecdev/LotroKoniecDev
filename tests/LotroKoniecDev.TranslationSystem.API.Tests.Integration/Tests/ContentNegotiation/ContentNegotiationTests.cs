using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.ContentNegotiation;

/// <summary>
/// Pins what caches depend on: the media type follows <c>Accept</c>, and <c>Vary: Accept</c> is sent for
/// both forms (RFC 9110 §12.5.5).
/// </summary>
[Collection("TranslationApi")]
public sealed class ContentNegotiationTests
{
    private const string ListRoute = "/api/v1/translations";

    // Written out, not taken from MediaTypes.HateoasJson, because it is the public wire contract.
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
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(expectedMediaType);
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
