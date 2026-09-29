using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
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
        using ServiceProvider provider = CreateFrontendAuthenticationServices().BuildServiceProvider();

        List<HttpMessageHandler> chain = HttpMessageHandlerChain.From(provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(ITokenEndpointClient)));

        chain[^1].ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    /// <summary>
    /// #923: the refresh runs before the page renders, so the default of 100 seconds would hold the page
    /// that long when the auth API stalls.
    /// </summary>
    [Fact]
    public void AddFrontendAuthentication_TokenEndpointClient_UsesTheDefaultRequestTimeout()
    {
        using ServiceProvider provider = CreateFrontendAuthenticationServices().BuildServiceProvider();

        using HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(ITokenEndpointClient));

        client.Timeout.ShouldBe(HttpClientsDependencyInjectionExtensions.DefaultRequestTimeout);
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

    [Fact]
    public void AddFrontendAuthentication_CookieOptions_DevelopmentEnvironment_UsesSameAsRequest()
    {
        CookieAuthenticationOptions options = ResolveConfiguredCookieOptions("Development");

        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.SameAsRequest);
    }

    private static OpenIdConnectOptions ResolveConfiguredOidcOptions()
    {
        ServiceCollection services = CreateFrontendAuthenticationServices();
        using ServiceProvider provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
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
