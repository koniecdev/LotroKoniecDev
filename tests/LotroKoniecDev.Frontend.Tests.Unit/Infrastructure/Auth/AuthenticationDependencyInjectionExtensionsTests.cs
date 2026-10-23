using System.Globalization;
using System.Net;
using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth;

public sealed class AuthenticationDependencyInjectionExtensionsTests
{
    /// <summary>
    /// #693: OpenIddict's form_post page submits itself with an inline script, which the auth origin's CSP
    /// blocks.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectOptions_UsesQueryResponseMode()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        options.ResponseMode.ShouldBe(OpenIdConnectResponseMode.Query);
    }

    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectOptions_UsesPkce()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        options.UsePkce.ShouldBeTrue();
    }

    /// <summary>
    /// #1025: <c>SaveTokens</c> writes only <c>expires_at</c>. A token already dead on arrival is due at once.
    /// </summary>
    [Theory]
    [InlineData(300, 240.0)]
    [InlineData(121, 61.0)]
    [InlineData(120, 60.0)]
    [InlineData(60, 30.0)]
    [InlineData(1, 0.5)]
    [InlineData(0, 0.0)]
    [InlineData(-30, -30.0)]
    public async Task AddFrontendAuthentication_OnTicketReceived_StoresTheRefreshTimeOfTheFirstToken(
        int secondsUntilExpiry,
        double secondsUntilRefresh)
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();
        options.TimeProvider = time;
        AuthenticationProperties properties = PropertiesWith(
            new AuthenticationToken { Name = "access_token", Value = "access-token" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = time.GetUtcNow().AddSeconds(secondsUntilExpiry).ToString("o", CultureInfo.InvariantCulture)
            });

        await options.Events.TicketReceived(CreateTicketReceivedContext(options, properties));

        DateTimeOffset.Parse(properties.GetTokenValue("refresh_at").ShouldNotBeNull(), CultureInfo.InvariantCulture)
            .ShouldBe(time.GetUtcNow().AddSeconds(secondsUntilRefresh));
        properties.GetTokenValue("access_token").ShouldBe("access-token");
    }

    /// <summary>
    /// The cookie check then stops at the missing <c>expires_at</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-date")]
    public async Task AddFrontendAuthentication_OnTicketReceivedWithoutAUsableExpiry_StoresNoRefreshTime(string? expiresAt)
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();
        List<AuthenticationToken> tokens = [new AuthenticationToken { Name = "access_token", Value = "access-token" }];
        if (expiresAt is not null)
        {
            tokens.Add(new AuthenticationToken { Name = "expires_at", Value = expiresAt });
        }

        AuthenticationProperties properties = PropertiesWith([.. tokens]);

        await options.Events.TicketReceived(CreateTicketReceivedContext(options, properties));

        properties.GetTokenValue("refresh_at").ShouldBeNull();
        properties.GetTokenValue("access_token").ShouldBe("access-token");
    }

    /// <summary>
    /// #899: a followed redirect would carry the caller key, the visitor's address and the login code to
    /// wherever it pointed.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannel_DoesNotFollowRedirects()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        List<HttpMessageHandler> chain = HttpMessageHandlerChain.From(options.BackchannelHttpHandler);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    /// <summary>
    /// #922: a handler set before this registration is kept.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannelSetElsewhere_SwitchesOffTheRedirectsAndCookiesOfTheTransportInsideIt()
    {
        SocketsHttpHandler transport = new();
        PassThroughHandler handlerSetElsewhere = new() { InnerHandler = transport };

        OpenIdConnectOptions options = ResolveConfiguredOidcOptions(handlerSetElsewhere);

        HttpMessageHandlerChain.From(options.BackchannelHttpHandler).ShouldContain(handlerSetElsewhere);
        transport.AllowAutoRedirect.ShouldBeFalse();
        transport.UseCookies.ShouldBeFalse();
    }

    /// <summary>#922: the older transport, set as the back-channel handler itself.</summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannelSetElsewhere_SwitchesOffTheRedirectsAndCookiesOfAnHttpClientHandler()
    {
        HttpClientHandler transport = new();

        OpenIdConnectOptions options = ResolveConfiguredOidcOptions(transport);

        HttpMessageHandlerChain.From(options.BackchannelHttpHandler)[^1].ShouldBeSameAs(transport);
        transport.AllowAutoRedirect.ShouldBeFalse();
        transport.UseCookies.ShouldBeFalse();
    }

    /// <summary>#923: not the 100-second default.</summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_WaitsThirtySecondsAtMost()
    {
        using ServiceProvider provider = CreateFrontendAuthenticationServices().BuildServiceProvider();

        using HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(ITokenEndpointClient));

        client.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>#923: not the 60-second default.</summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannel_WaitsThirtySecondsAtMost()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        options.Backchannel.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// #923: the website's other clients to the sign-in server retry a 503, and this one must not.
    /// </summary>
    [Fact]
    public async Task AddFrontendAuthentication_TokenEndpointClient_SendsAFailedRenewalOnlyOnce()
    {
        UnavailableSignInServer signInServer = new();
        ServiceCollection services = CreateFrontendAuthenticationServices();
        services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>()
            .ConfigurePrimaryHttpMessageHandler(() => signInServer);
        await using ServiceProvider provider = services.BuildServiceProvider();
        ITokenEndpointClient client = provider.GetRequiredService<ITokenEndpointClient>();

        await client.RefreshAsync("the-refresh-token");

        signInServer.Requests.ShouldBe(1);
    }

    /// <summary>
    /// The revoke follows a link from the discovery document (#964, #830).
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_ChecksTheOriginBeforeAnyOtherFrontendHandler()
    {
        List<HttpMessageHandler> chain = ResolveTokenEndpointClientHandlerChain();

        chain.First(handler => handler.GetType().Assembly == typeof(SameOriginDelegatingHandler).Assembly)
            .ShouldBeOfType<SameOriginDelegatingHandler>();
    }

    /// <summary>#924: one handler serves every visitor's code exchange and userinfo call.</summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannel_KeepsNoCookies()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        List<HttpMessageHandler> chain = HttpMessageHandlerChain.From(options.BackchannelHttpHandler);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().UseCookies.ShouldBeFalse();
    }

    /// <summary>
    /// #1026: the remote-failure log is the only place that says why. The full sign-in is proved in the
    /// integration suite.
    /// </summary>
    [Theory]
    [InlineData(null, "300", "missing, empty or blank access_token")]
    [InlineData("   ", "300", "missing, empty or blank access_token")]
    [InlineData("the-access-token", null, "expires_in: missing or unreadable")]
    [InlineData("the-access-token", "soon", "expires_in: missing or unreadable")]
    [InlineData("the-access-token", "0", "expires_in: 0")]
    [InlineData("the-access-token", "-5", "expires_in: -5")]
    [InlineData("the-access-token", "300", "blank refresh_token", " ")]
    [InlineData("the-access-token", "300", "blank refresh_token", "\t\r\n")]
    public async Task AddFrontendAuthentication_TokenResponseReceived_FailsAnUnusableAnswerWithTheReason(
        string? accessToken,
        string? expiresIn,
        string expectedReason,
        string? refreshToken = "the-refresh-token")
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();
        TokenResponseReceivedContext context = CreateTokenResponseReceivedContext(
            options, accessToken, expiresIn, refreshToken);

        await options.Events.TokenResponseReceived(context);

        context.Result.ShouldNotBeNull().Failure.ShouldNotBeNull().Message.ShouldContain(expectedReason);
    }

    /// <summary>
    /// OAuth makes the refresh token optional, and the handler stores no empty one.
    /// </summary>
    [Theory]
    [InlineData("1", "the-refresh-token")]
    [InlineData("300", "the-refresh-token")]
    [InlineData("300", null)]
    [InlineData("300", "")]
    public async Task AddFrontendAuthentication_TokenResponseReceived_LetsAUsableAnswerThrough(
        string expiresIn,
        string? refreshToken)
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();
        TokenResponseReceivedContext context = CreateTokenResponseReceivedContext(
            options, "the-access-token", expiresIn, refreshToken);

        await options.Events.TokenResponseReceived(context);

        context.Result.ShouldBeNull();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void AddFrontendAuthentication_CookieOptions_NonDevelopmentEnvironment_RequiresSecureCookieUnconditionally(
        string environmentName)
    {
        CookieAuthenticationOptions options = ResolveConfiguredCookieOptions(environmentName);

        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
    }

    /// <summary>
    /// #1014: the auth API's refresh token lives 9 hours. A longer idle time would keep a cookie whose
    /// token is already dead.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_CookieOptions_EndsAfterEightIdleHours()
    {
        CookieAuthenticationOptions options = ResolveConfiguredCookieOptions("Production");

        options.ExpireTimeSpan.ShouldBe(TimeSpan.FromHours(8));
        options.SlidingExpiration.ShouldBeTrue();
    }

    [Fact]
    public void AddFrontendAuthentication_CookieOptions_DevelopmentEnvironment_UsesSameAsRequest()
    {
        CookieAuthenticationOptions options = ResolveConfiguredCookieOptions("Development");

        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.SameAsRequest);
    }

    private static TokenResponseReceivedContext CreateTokenResponseReceivedContext(
        OpenIdConnectOptions options,
        string? accessToken,
        string? expiresIn,
        string? refreshToken)
    {
        AuthenticationScheme scheme = new(
            OpenIdConnectDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(OpenIdConnectHandler));

        return new TokenResponseReceivedContext(
            new DefaultHttpContext(),
            scheme,
            options,
            new ClaimsPrincipal(),
            new AuthenticationProperties())
        {
            TokenEndpointResponse = new OpenIdConnectMessage
            {
                AccessToken = accessToken,
                ExpiresIn = expiresIn,
                RefreshToken = refreshToken
            }
        };
    }

    private static AuthenticationProperties PropertiesWith(params AuthenticationToken[] tokens)
    {
        AuthenticationProperties properties = new();
        properties.StoreTokens(tokens);
        return properties;
    }

    private static TicketReceivedContext CreateTicketReceivedContext(
        OpenIdConnectOptions options,
        AuthenticationProperties properties)
    {
        AuthenticationScheme scheme = new(
            OpenIdConnectDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(OpenIdConnectHandler));
        AuthenticationTicket ticket = new(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject")], "test")),
            properties,
            OpenIdConnectDefaults.AuthenticationScheme);

        return new TicketReceivedContext(new DefaultHttpContext(), scheme, options, ticket);
    }

    private static OpenIdConnectOptions ResolveConfiguredOidcOptions(HttpMessageHandler? backchannelHandlerSetFirst = null)
    {
        ServiceCollection services = CreateFrontendAuthenticationServices(backchannelHandlerSetFirst);
        using ServiceProvider provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
    }

    private static List<HttpMessageHandler> ResolveTokenEndpointClientHandlerChain()
    {
        using ServiceProvider provider = CreateFrontendAuthenticationServices().BuildServiceProvider();

        return HttpMessageHandlerChain.From(provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(ITokenEndpointClient)));
    }

    private static CookieAuthenticationOptions ResolveConfiguredCookieOptions(string environmentName)
    {
        ServiceCollection services = CreateFrontendAuthenticationServices();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        services.AddSingleton(environment);
        using ServiceProvider provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static ServiceCollection CreateFrontendAuthenticationServices(HttpMessageHandler? backchannelHandlerSetFirst = null)
    {
        ServiceCollection services = new();
        if (backchannelHandlerSetFirst is not null)
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = backchannelHandlerSetFirst);
        }

        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = "https://localhost:5003",
            Authority = "https://localhost:5003",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"],
        }));
        services.AddFrontendAuthentication();

        return services;
    }

    /// <summary>Stands for a handler that other code put in front of its own transport.</summary>
    private sealed class PassThroughHandler : DelegatingHandler;

    /// <summary>A sign-in server that answers every request with 503 and counts them.</summary>
    private sealed class UnavailableSignInServer : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
