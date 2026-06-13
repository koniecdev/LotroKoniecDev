using System.Net.Http.Headers;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Auth;

[Collection("TranslationApi")]
public sealed class AuthorizationDefaultsTests
{
    private readonly TranslationSystemApiFactory _factory;

    public AuthorizationDefaultsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetDiscovery_WithoutToken_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDiscovery_WithValidToken_ShouldReturn200AndDiscoveryResponse()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken());

        // Act
        HttpResponseMessage response = await client.GetAsync("/");
        DiscoveryResponse? body = await response.Content.ReadFromJsonAsync<DiscoveryResponse>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("LotroKoniecDev.TranslationSystem");
    }

    [Fact]
    public async Task GetDiscovery_WithTokenSignedByUnknownKey_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateTokenSignedWithUnknownKey());

        // Act
        HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDiscovery_WithExpiredToken_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateExpiredAccessToken());

        // Act
        HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUnknownRoute_WithoutToken_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/does-not-exist");

        // Assert
        // The fallback policy covers even unmatched paths — anonymous requests cannot
        // enumerate routes by distinguishing 401 from 404.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUnknownRoute_WithValidToken_ShouldReturn404()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken());

        // Act
        HttpResponseMessage response = await client.GetAsync("/does-not-exist");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
