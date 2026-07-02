using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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

    private static OpenIdConnectOptions ResolveConfiguredOidcOptions()
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
        using ServiceProvider provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
    }
}
