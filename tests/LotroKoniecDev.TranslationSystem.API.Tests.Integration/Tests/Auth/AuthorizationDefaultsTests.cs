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

    [Fact]
    public async Task GetProtectedResource_WithExpiredToken_ShouldReturn401()
    {
        // Arrange — token rejection is enforced on every protected route, not just discovery.
        // (/api/v1/game-versions: the translations list itself is publicly readable since #309.)
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateExpiredAccessToken());

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProtectedResource_WithTokenSignedByUnknownKey_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateTokenSignedWithUnknownKey());

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTranslatorResource_WithUnrecognizedRole_ShouldReturn403()
    {
        // Arrange — a correctly-signed token whose role is neither Admin nor Translator is
        // authenticated, but the RequireTranslatorRole policy still rejects it.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken("Reviewer"));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
