using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
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
        // Arrange: the one deliberate hole in authorized-by-default (#608). What it returns per caller
        // is DiscoveryHateoasTests' subject.
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
        // Unknown paths get 401 too, so an anonymous caller cannot map the API by 401 versus 404.
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

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(1200)]
    public async Task GetProtectedResource_WithExpiredToken_ShouldReturn401(int secondsSinceExpiry)
    {
        // Arrange: game versions, because the translations list is public (#309). Under 300 seconds is
        // inside JwtBearer's default skew; the TMS allows none (#933, ADR-0049).
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessTokenExpiringAt(DateTime.UtcNow.AddSeconds(-secondsSinceExpiry)));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProtectedResource_WithTokenExpiringInAMinute_ShouldReturn200()
    {
        // Arrange: the other side of the zero skew.
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessTokenExpiringAt(DateTime.UtcNow.AddMinutes(1)));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(AuthConstants.TokenTypes.AccessToken)]
    [InlineData(AuthConstants.TokenTypes.AccessTokenMediaType)]
    public async Task GetProtectedResource_WithAccessTokenType_ShouldReturn200(string tokenType)
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessTokenOfType(tokenType));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("JWT")]
    [InlineData("application/JWT")]
    [InlineData("oi_auc+jwt")]
    public async Task GetProtectedResource_WithSignedTokenOfAnotherType_ShouldReturn401(string tokenType)
    {
        // Arrange: only the type is wrong. `JWT` is what the auth server writes on an ID token (#933,
        // RFC 9068).
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TranslationSystemApiFactory.CreateAccessTokenOfType(tokenType));

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
    public async Task GetProtectedResource_WithATokenThatNamesBothApis_ShouldReturn200()
    {
        // Arrange: the website sends one token to both APIs, so it names the auth API too (#1023)
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessTokenForAudiences(
                TranslationSystemApiFactory.TestAudience,
                AuthConstants.Audiences.AuthApi));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProtectedResource_WithATokenThatNamesOnlyTheAuthApi_ShouldReturn401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessTokenForAudiences(AuthConstants.Audiences.AuthApi));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTranslatorResource_WithUnrecognizedRole_ShouldReturn403()
    {
        // Arrange: authenticated, but neither Admin nor Translator.
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
        // Arrange: proves each endpoint's own binding, not just the policy. Authorization answers before
        // the endpoint runs, so the write route needs no body.
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
