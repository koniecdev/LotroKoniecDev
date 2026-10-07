using System.Globalization;
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
    /// #1025: the first sign-in stores a refresh time too. SaveTokens writes only <c>expires_at</c>, and with
    /// the old fixed 60 seconds a one-minute token was due on the very first page. A token that was already
    /// dead on arrival is due at once, never later than its expiry.
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
    /// With no usable expiry there is nothing to plan from, and the cookie check stops at the missing
    /// <c>expires_at</c> as it always did.
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
