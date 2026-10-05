using System.Net;
using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth;

/// <summary>
/// Calls the login route's handler directly, with no web host. When the OIDC authority answers, it has
/// to produce the challenge and a 302. When the discovery fetch fails, because the auth server is down,
/// it has to return a 503, so the status-code pages show the friendly "login unavailable" page instead
/// of the challenge throwing a raw 500 (#311).
/// The logout route's handler revokes the website's refresh token itself before it hands the sign-out to
/// the browser, and never lets that revoke stop the sign-out (#964).
/// </summary>
public sealed class AuthEndpointsExtensionsTests
{
    private const string Authority = "https://auth.lotro.test";
    private const string RevocationEndpoint = "https://auth.lotro.test/connect/revoke";
    private const string RefreshToken = "the-refresh-token";
    private const string IdToken = "the-id-token";

    public static TheoryData<Exception> AuthorityUnreachableFailures() => new()
    {
        new InvalidOperationException("IDX20803: Unable to obtain configuration from the authority."),
        new HttpRequestException("Connection refused."),
        new TaskCanceledException("The discovery request timed out."),
    };

    [Theory]
    [MemberData(nameof(AuthorityUnreachableFailures))]
    public async Task LoginAsync_WhenDiscoveryFetchFails_ReturnsServiceUnavailableInsteadOfRaw500(
        Exception discoveryFailure)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OpenIdConnectConfiguration>(discoveryFailure));

        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(configurationManager),
            NullLoggerFactory.Instance);

        StatusCodeHttpResult statusResult = result.ShouldBeOfType<StatusCodeHttpResult>();
        statusResult.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task LoginAsync_WhenAuthorityReachable_ReturnsTheOidcChallenge()
    {
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(CreateReachableConfigurationManager()),
            NullLoggerFactory.Instance);

        ChallengeHttpResult challenge = result.ShouldBeOfType<ChallengeHttpResult>();
        challenge.AuthenticationSchemes.ShouldHaveSingleItem()
            .ShouldBe(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [Theory]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/translations?status=NeedsReview", "/translations?status=NeedsReview")]
    [InlineData("https://evil.example.com/harvest", "/")]
    [InlineData("//evil.example.com", "/")]
    [InlineData("/\\evil.example.com", "/")]
    // Browsers strip ASCII tab/newline, so this would resolve to the protocol-relative
    // "//evil.example.com" once the challenge's RedirectUri reaches the address bar.
    [InlineData("/\t/evil.example.com", "/")]
    [InlineData("/\r\n/evil.example.com", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public async Task LoginAsync_WhenAuthorityReachable_SanitizesReturnUrlToALocalRedirect(
        string? returnUrl, string expectedRedirectUri)
    {
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            returnUrl,
            CreateOptionsMonitor(CreateReachableConfigurationManager()),
            NullLoggerFactory.Instance);

        ChallengeHttpResult challenge = result.ShouldBeOfType<ChallengeHttpResult>();
        challenge.Properties.ShouldNotBeNull();
        challenge.Properties!.RedirectUri.ShouldBe(expectedRedirectUri);
    }

    [Fact]
    public async Task LoginAsync_WhenNoConfigurationManager_StillReturnsTheOidcChallenge()
    {
        // Once the OIDC handler post-configures, ConfigurationManager is always set; the null-guard
        // mirrors CookieTokenRefresher so a missing manager can never turn a login into a false 503.
        IResult result = await AuthEndpointsExtensions.LoginAsync(
            new DefaultHttpContext(),
            "/dashboard",
            CreateOptionsMonitor(configurationManager: null),
            NullLoggerFactory.Instance);

        result.ShouldBeOfType<ChallengeHttpResult>();
    }

    [Fact]
    public async Task LocalSignOutAsync_SignsOutTheCookieAndRedirectsToTheLocalReturnUrl()
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        HttpContext context = CreateContextWith(authenticationService);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context,
            "/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z");

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z");
        // The cookie sign-out does not show up in the return value, so the .Received() check is the only
        // way to see it happened.
        await authenticationService.Received(1).SignOutAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Theory]
    [InlineData("https://evil.example.com/harvest")]
    [InlineData("//evil.example.com")]
    [InlineData("/\\evil.example.com")]
    [InlineData("/\t/evil.example.com")]
    [InlineData("/\r\n/evil.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task LocalSignOutAsync_WhenReturnUrlIsNotLocal_RedirectsHome(string? returnUrl)
    {
        HttpContext context = CreateContextWith(Substitute.For<IAuthenticationService>());

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(context, returnUrl);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/");
    }

    [Fact]
    public async Task LogoutAsync_RevokesTheRefreshTokenAtTheDiscoveredEndpoint()
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), RefreshToken);

        await AuthEndpointsExtensions.LogoutAsync(context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        authApi.LastRequest.ShouldNotBeNull().RequestUri.ShouldBe(new Uri(RevocationEndpoint));
        authApi.LastRequestBody.ShouldNotBeNull().ShouldContain($"token={RefreshToken}");
    }

    [Fact]
    public async Task LogoutAsync_SignsOutTheCookieAndRedirectsToTheEndSessionPageWithTheHint()
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        HttpContext context = CreateSignedInContext(authenticationService, RefreshToken);

        IResult result = await AuthEndpointsExtensions.LogoutAsync(
            context,
            CreateAuthSystemOptions(),
            CreateRevoker(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty)));

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe(
            $"{Authority}/connect/logout?post_logout_redirect_uri={Uri.EscapeDataString("https://app.lotro.test")}"
            + $"&id_token_hint={IdToken}");
        // The cookie sign-out does not show up in the return value, so the .Received() check is the only
        // way to see it happened.
        await authenticationService.Received(1).SignOutAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    /// <summary>
    /// A closed tab aborts the sign-out request. That is the case the revoke exists for, so the abort must
    /// not cancel it.
    /// </summary>
    [Fact]
    public async Task LogoutAsync_WhenTheBrowserDropsTheRequest_StillRevokesTheRefreshToken()
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), RefreshToken);
        context.RequestAborted = new CancellationToken(canceled: true);

        await AuthEndpointsExtensions.LogoutAsync(context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        authApi.LastRequestBody.ShouldNotBeNull().ShouldContain($"token={RefreshToken}");
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("timed out")]
    [InlineData("503")]
    [InlineData("400")]
    public async Task LogoutAsync_WhenTheRevokeFails_StillSignsOutAndRedirectsToTheEndSessionPage(string failure)
    {
        StubHttpMessageHandler authApi = failure switch
        {
            "connection refused" => StubHttpMessageHandler.Throw(new HttpRequestException("Connection refused.")),
            "timed out" => StubHttpMessageHandler.Throw(new TaskCanceledException("The revoke timed out.")),
            "503" => StubHttpMessageHandler.RespondWith(HttpStatusCode.ServiceUnavailable, string.Empty),
            _ => StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, """{"error":"invalid_request"}""")
        };
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        HttpContext context = CreateSignedInContext(authenticationService, RefreshToken);

        IResult result = await AuthEndpointsExtensions.LogoutAsync(
            context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        result.ShouldBeOfType<RedirectHttpResult>().Url.ShouldStartWith($"{Authority}/connect/logout?");
        await authenticationService.Received(1).SignOutAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task LogoutAsync_WhenTheCookieHoldsNoRefreshToken_RevokesNothingAndStillRedirects(string? refreshToken)
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), refreshToken);

        IResult result = await AuthEndpointsExtensions.LogoutAsync(
            context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        authApi.LastRequest.ShouldBeNull();
        result.ShouldBeOfType<RedirectHttpResult>().Url.ShouldContain($"&id_token_hint={IdToken}");
    }

    /// <summary>
    /// A visitor with no session, or one whose session the cookie check just rejected, has no tokens to
    /// read. The sign-out still ends on the auth server's page, without a hint.
    /// </summary>
    [Fact]
    public async Task LogoutAsync_WhenThereIsNoSession_RevokesNothingAndRedirectsWithoutAHint()
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.NoResult());
        HttpContext context = CreateContextWith(authenticationService);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.lotro.test");

        IResult result = await AuthEndpointsExtensions.LogoutAsync(
            context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        authApi.LastRequest.ShouldBeNull();
        result.ShouldBeOfType<RedirectHttpResult>().Url.ShouldNotContain("id_token_hint");
    }

    private static HttpContext CreateSignedInContext(IAuthenticationService authenticationService, string? refreshToken)
    {
        List<AuthenticationToken> tokens = [new AuthenticationToken { Name = "id_token", Value = IdToken }];
        if (refreshToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = "refresh_token", Value = refreshToken });
        }

        AuthenticationProperties properties = new();
        properties.StoreTokens(tokens);
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim("sub", "user-1")], authenticationType: "Cookies"));
        authenticationService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, "Cookies")));

        HttpContext context = CreateContextWith(authenticationService);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.lotro.test");
        return context;
    }

    private static IOptions<AuthSystemSettings> CreateAuthSystemOptions() =>
        Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = Authority + "/",
            Authority = Authority,
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"]
        });

    /// <summary>The real revoker and token client, with the auth API and its discovery document stubbed.</summary>
    private static RefreshTokenRevoker CreateRevoker(StubHttpMessageHandler authApi)
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenIdConnectConfiguration { RevocationEndpoint = RevocationEndpoint }));

        TokenEndpointClient tokenEndpointClient = new(
            new HttpClient(authApi) { BaseAddress = new Uri(Authority + "/") },
            CreateAuthSystemOptions(),
            NullLogger<TokenEndpointClient>.Instance);

        return new RefreshTokenRevoker(
            tokenEndpointClient,
            CreateOptionsMonitor(configurationManager),
            TimeProvider.System,
            NullLogger<RefreshTokenRevoker>.Instance);
    }

    private static HttpContext CreateContextWith(IAuthenticationService authenticationService)
    {
        ServiceCollection services = new();
        services.AddSingleton(authenticationService);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static IOptionsMonitor<OpenIdConnectOptions> CreateOptionsMonitor(
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager)
    {
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor =
            Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configurationManager });
        return optionsMonitor;
    }

    private static IConfigurationManager<OpenIdConnectConfiguration> CreateReachableConfigurationManager()
    {
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
            Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configurationManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenIdConnectConfiguration()));
        return configurationManager;
    }
}
