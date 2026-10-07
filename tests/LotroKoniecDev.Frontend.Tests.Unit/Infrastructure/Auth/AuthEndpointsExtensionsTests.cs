using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Web;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        HttpContext context = CreateSignedInContext(authenticationService, RefreshToken);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context,
            "/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z",
            CreateRevoker(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty)));

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
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), RefreshToken);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context, returnUrl, CreateRevoker(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty)));

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/");
    }

    /// <summary>
    /// #1027: the auth server revokes the account's tokens when it schedules the deletion, but only as best
    /// effort, so the website revokes the refresh token it held as well.
    /// </summary>
    [Fact]
    public async Task LocalSignOutAsync_WhenTheCookieHoldsARefreshToken_RevokesItAtTheDiscoveredEndpoint()
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), RefreshToken);

        await AuthEndpointsExtensions.LocalSignOutAsync(context, "/account/deletion-scheduled", CreateRevoker(authApi));

        authApi.LastRequest.ShouldNotBeNull().RequestUri.ShouldBe(new Uri(RevocationEndpoint));
        authApi.LastRequestBody.ShouldNotBeNull().ShouldContain($"token={RefreshToken}");
    }

    /// <summary>
    /// A visitor with no cookie, or with an expired or unreadable one, has no token to read. The sign-out
    /// still ends on the local page, and the revoke sends nothing.
    /// </summary>
    [Theory]
    [InlineData("no cookie")]
    [InlineData("expired or unreadable cookie")]
    public async Task LocalSignOutAsync_WhenThereIsNoSession_RevokesNothingAndStillRedirects(string session)
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(session == "no cookie" ? AuthenticateResult.NoResult() : AuthenticateResult.Fail("No principal."));
        HttpContext context = CreateContextWith(authenticationService);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context, "/account/deletion-scheduled", CreateRevoker(authApi));

        authApi.LastRequest.ShouldBeNull();
        result.ShouldBeOfType<RedirectHttpResult>().Url.ShouldBe("/account/deletion-scheduled");
    }

    /// <summary>
    /// Through the real cookie handler and its token check, like the logout above. The check skips this
    /// request, so no refresh redeems the stored token before the sign-out reads it, and that stored token is
    /// the one revoked, exactly once (#1027).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalSignOutAsync_ThroughTheRealCookieCheckWhenDroppedOrNearExpiry_RevokesOnlyTheStoredRefreshToken(
        bool browserDroppedTheRequest,
        bool accessTokenNearItsEnd)
    {
        RecordingAuthApi authApi = new();
        await using ServiceProvider provider = BuildRealAuthentication(authApi);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        HttpContext context = CreateSignOutRequest(
            scope.ServiceProvider,
            accessTokenExpiresAt: accessTokenNearItsEnd
                ? DateTimeOffset.UtcNow.AddSeconds(30)
                : DateTimeOffset.UtcNow.AddMinutes(4),
            path: AuthenticationDependencyInjectionExtensions.LocalSignOutPath);
        if (browserDroppedTheRequest)
        {
            context.RequestAborted = new CancellationToken(canceled: true);
        }

        await AuthEndpointsExtensions.LocalSignOutAsync(
            context,
            "/account/deletion-scheduled",
            scope.ServiceProvider.GetRequiredService<RefreshTokenRevoker>());

        authApi.Requests.ShouldHaveSingleItem().ShouldBe($"{RevocationEndpoint} token={RefreshToken}");
    }

    [Fact]
    public async Task LocalSignOutAsync_WhenTheRevokeFails_StillSignsOutAndRedirects()
    {
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        HttpContext context = CreateSignedInContext(authenticationService, RefreshToken);

        IResult result = await AuthEndpointsExtensions.LocalSignOutAsync(
            context,
            "/account/deletion-scheduled",
            CreateRevoker(StubHttpMessageHandler.Throw(new InvalidOperationException("A new handler failed."))));

        result.ShouldBeOfType<RedirectHttpResult>().Url.ShouldBe("/account/deletion-scheduled");
        await authenticationService.Received(1).SignOutAsync(
            context,
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Fact]
    public async Task LogoutAsync_WhenTheCookieHoldsARefreshToken_RevokesItAtTheDiscoveredEndpoint()
    {
        StubHttpMessageHandler authApi = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        HttpContext context = CreateSignedInContext(Substitute.For<IAuthenticationService>(), RefreshToken);

        await AuthEndpointsExtensions.LogoutAsync(context, CreateAuthSystemOptions(), CreateRevoker(authApi));

        authApi.LastRequest.ShouldNotBeNull().RequestUri.ShouldBe(new Uri(RevocationEndpoint));
        authApi.LastRequestBody.ShouldNotBeNull().ShouldContain($"token={RefreshToken}");
    }

    [Fact]
    public async Task LogoutAsync_WhenSignedIn_SignsOutTheCookieAndRedirectsToTheEndSessionPageWithTheHint()
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
    /// Through the real cookie handler and its token check. A closed tab aborts the sign-out request, and
    /// an access token near its end would normally be refreshed first. Neither may stop the revoke, and the
    /// token revoked must be the stored one: a refresh would leave it redeemed but still accepted for
    /// OpenIddict's reuse window, so a copied cookie could renew itself (#964).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LogoutAsync_ThroughTheRealCookieCheckWhenDroppedOrNearExpiry_RevokesOnlyTheStoredRefreshToken(
        bool browserDroppedTheRequest,
        bool accessTokenNearItsEnd)
    {
        RecordingAuthApi authApi = new();
        await using ServiceProvider provider = BuildRealAuthentication(authApi);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        HttpContext context = CreateSignOutRequest(
            scope.ServiceProvider,
            accessTokenExpiresAt: accessTokenNearItsEnd
                ? DateTimeOffset.UtcNow.AddSeconds(30)
                : DateTimeOffset.UtcNow.AddMinutes(4));
        if (browserDroppedTheRequest)
        {
            context.RequestAborted = new CancellationToken(canceled: true);
        }

        await AuthEndpointsExtensions.LogoutAsync(
            context,
            CreateAuthSystemOptions(),
            scope.ServiceProvider.GetRequiredService<RefreshTokenRevoker>());

        authApi.Requests.ShouldHaveSingleItem().ShouldBe($"{RevocationEndpoint} token={RefreshToken}");
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("timed out")]
    [InlineData("503")]
    [InlineData("400")]
    [InlineData("unexpected exception")]
    public async Task LogoutAsync_WhenTheRevokeFails_StillSignsOutAndRedirectsToTheEndSessionPage(string failure)
    {
        StubHttpMessageHandler authApi = failure switch
        {
            "connection refused" => StubHttpMessageHandler.Throw(new HttpRequestException("Connection refused.")),
            "timed out" => StubHttpMessageHandler.Throw(new TaskCanceledException("The revoke timed out.")),
            "503" => StubHttpMessageHandler.RespondWith(HttpStatusCode.ServiceUnavailable, string.Empty),
            "unexpected exception" => StubHttpMessageHandler.Throw(new InvalidOperationException("A new handler failed.")),
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
    [InlineData("   ")]
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

    /// <summary>
    /// The frontend's own authentication registration. Only the boundaries are swapped: the auth API's
    /// transport, its discovery document and the clock-free host environment.
    /// </summary>
    private static ServiceProvider BuildRealAuthentication(HttpMessageHandler authApi)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(environment);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(CreateAuthSystemOptions());
        services.AddSingleton(Substitute.For<IDeadSessionRegistry>());
        services.AddSingleton(Substitute.For<ISessionExpiryNotice>());
        services.AddFrontendAuthentication();
        services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>()
            .ConfigurePrimaryHttpMessageHandler(() => authApi);
        services.PostConfigure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Authority, RevocationEndpoint = RevocationEndpoint }));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A sign-out POST that carries a real, protected session cookie. Its access token is not a signed
    /// token, so any check of it would end the session.
    /// </summary>
    private static HttpContext CreateSignOutRequest(
        IServiceProvider requestServices,
        DateTimeOffset accessTokenExpiresAt,
        string path = AuthenticationDependencyInjectionExtensions.LogoutPath)
    {
        AuthenticationProperties properties = new();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "not-a-signed-token" },
            new AuthenticationToken { Name = "refresh_token", Value = RefreshToken },
            new AuthenticationToken { Name = "id_token", Value = IdToken },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = accessTokenExpiresAt.ToString("o", CultureInfo.InvariantCulture)
            }
        ]);
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", "user-1")], CookieAuthenticationDefaults.AuthenticationScheme));
        CookieAuthenticationOptions cookieOptions = requestServices
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme));

        DefaultHttpContext context = new() { RequestServices = requestServices };
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.lotro.test");
        context.Request.Path = path;
        context.Request.Headers.Cookie = $"{cookieOptions.Cookie.Name}={protectedTicket}";
        return context;
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

    /// <summary>Records every call to the auth API as "address body" and answers each with 200.</summary>
    private sealed class RecordingAuthApi : HttpMessageHandler
    {
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string token = HttpUtility.ParseQueryString(body)["token"] ?? string.Empty;
            _requests.Add($"{request.RequestUri} token={token}");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
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
