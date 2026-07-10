using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Settings;
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
