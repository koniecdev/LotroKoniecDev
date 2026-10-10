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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth;

public sealed class AuthenticationDependencyInjectionExtensionsTests
{
    /// <summary>
    /// Load-bearing for the auth origin's CSP (#693): OpenIddict's form_post page submits itself with an
    /// inline script, which <c>script-src 'self'</c> there blocks, so form_post would break sign-in.
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
    /// #899: a followed redirect would carry the caller key, the visitor's address and, on a 307/308, the
    /// refresh token to wherever the auth API pointed.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_DoesNotFollowRedirects()
    {
        List<HttpMessageHandler> chain = ResolveTokenEndpointClientHandlerChain();

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    /// <summary>
    /// #899: the same for the code exchange, which carries the login code in its form, and for the
    /// userinfo and metadata calls.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannel_DoesNotFollowRedirects()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        List<HttpMessageHandler> chain = HttpMessageHandlerChain.From(options.BackchannelHttpHandler);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    /// <summary>#924: one handler serves every visitor's refresh.</summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_KeepsNoCookies()
    {
        List<HttpMessageHandler> chain = ResolveTokenEndpointClientHandlerChain();

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().UseCookies.ShouldBeFalse();
    }

    /// <summary>
    /// #964: the sign-out's revoke follows a link from the discovery document, so the origin check has to
    /// run before the caller key is added (#830).
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_ChecksTheOriginBeforeAnyOtherFrontendHandler()
    {
        List<HttpMessageHandler> chain = ResolveTokenEndpointClientHandlerChain();

        chain.First(handler => handler.GetType().Assembly == typeof(SameOriginDelegatingHandler).Assembly)
            .ShouldBeOfType<SameOriginDelegatingHandler>();
    }

    /// <summary>#924: the same for every visitor's code exchange and userinfo call.</summary>
    [Fact]
    public void AddFrontendAuthentication_OpenIdConnectBackchannel_KeepsNoCookies()
    {
        OpenIdConnectOptions options = ResolveConfiguredOidcOptions();

        List<HttpMessageHandler> chain = HttpMessageHandlerChain.From(options.BackchannelHttpHandler);

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().UseCookies.ShouldBeFalse();
    }

    /// <summary>
    /// #1026: a refused sign-in ends on the error page, and the remote-failure log is the only place that
    /// says why. The full sign-in, with no session at the end, is proved in the Frontend integration suite.
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
    /// A missing or empty refresh token is allowed: OAuth makes it optional, and the handler stores no empty
    /// one.
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
    /// #1014: this cookie carries the refresh token, and the auth API lets that token live 9 hours from
    /// its last use. A longer idle time here would keep a cookie whose token is already dead.
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

    private static OpenIdConnectOptions ResolveConfiguredOidcOptions()
    {
        ServiceCollection services = CreateFrontendAuthenticationServices();
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

    private static ServiceCollection CreateFrontendAuthenticationServices()
    {
        ServiceCollection services = new();
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
}
