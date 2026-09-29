using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.TokenRefresh;

public sealed class TokenEndpointClientTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a token.
    private static readonly string AccessToken = new('a', 40);

    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string RefreshToken = "the-refresh-token";

    [Fact]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithTokens_ReturnsThem()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken}}","expires_in":3600}"""));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldNotBeNull().AccessToken.ShouldBe(AccessToken);
    }

    /// <summary>
    /// #899: the primary handler follows no redirect, so a redirect reaches this client as it is. It has
    /// to count as a failed refresh, the same as any other answer that carries no tokens.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithARedirect_ReturnsNull(HttpStatusCode statusCode)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWithHeaders(
            statusCode,
            new Dictionary<string, string> { ["Location"] = "https://attacker.example/connect/token" }));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
    }

    /// <summary>
    /// #923: the registration gives this client a short limit. When a call to a stalled auth API reaches
    /// it, the refresh has to fail like any other, so the page signs the user out instead of throwing.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheAuthApiNeverAnswers_ReturnsNullOnceTheClientTimesOut()
    {
        using HttpClient httpClient = new(new StalledHttpMessageHandler())
        {
            BaseAddress = new Uri(AuthBaseUrl),
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        TokenEndpointClient client = CreateClient(httpClient);

        TokenResponse? response = await client.RefreshAsync(RefreshToken).WaitAsync(TimeSpan.FromSeconds(10));

        response.ShouldBeNull();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler transport) =>
        new(transport) { BaseAddress = new Uri(AuthBaseUrl) };

    private static TokenEndpointClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = AuthBaseUrl,
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"]
        }),
        NullLogger<TokenEndpointClient>.Instance);

    /// <summary>
    /// An auth API that took the connection but never answers. Only cancellation ends the wait.
    /// </summary>
    private sealed class StalledHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay returned without being cancelled.");
        }
    }
}
