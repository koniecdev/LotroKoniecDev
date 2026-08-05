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
    public async Task GetDiscovery_WithoutToken_ShouldReturn200()
    {
        // Arrange — the service document is the one deliberate hole in authorized-by-default (#608):
        // it advertises only endpoints the caller may already reach, and an unauthenticated client
        // has no other way to bootstrap. What it hands back per caller is
        // DiscoveryHateoasTests' subject; here the point is only that the root is not walled off.
        using HttpClient client = _factory.CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

    [Theory]
    [InlineData("PUT", "/api/v1/translations")]
    [InlineData("GET", "/api/v1/translations/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/v1/translations/stats")]
    [InlineData("GET", "/api/v1/game-versions/11111111-1111-1111-1111-111111111111")]
    public async Task TranslatorGatedEndpoint_WithUnrecognizedRole_ShouldReturn403(string method, string route)
    {
        // Arrange — the same unrecognized-role token as above, sent at each translator-gated
        // route so every endpoint's own RequireTranslatorRole binding is proven, not just the
        // policy definition. Authorization short-circuits before the endpoint runs, so the write
        // route needs no request body.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessToken("Reviewer"));
        using HttpRequestMessage request = new(new HttpMethod(method), route);

        // Act
        HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
