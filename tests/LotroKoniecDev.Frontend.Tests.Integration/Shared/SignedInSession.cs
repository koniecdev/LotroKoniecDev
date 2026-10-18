using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>
/// The session cookie a sign-in leaves, protected with the host's own keys. Its access token is far from
/// expiry, so the cookie check has no renewal to make and never calls the sign-in server. The token is
/// opaque to the website, so any text serves.
/// </summary>
public static class SignedInSession
{
    public const string CookieName = ".lotrokoniecdev.auth";
    public const string Subject = "11111111-1111-1111-1111-111111111111";
    public const string UserName = "Anna";

    /// <summary>A browser that already carries the session cookie.</summary>
    public static CookieJarBrowser OpenBrowser(WebApplicationFactory<Program> host)
    {
        CookieJarBrowser browser = new(host);
        browser.SetCookie(CookieName, ProtectedTicket(host));
        return browser;
    }

    public static string ProtectedTicket(WebApplicationFactory<Program> host)
    {
        ClaimsPrincipal user = new(new ClaimsIdentity(
            [new Claim("sub", Subject), new Claim("name", UserName)],
            authenticationType: OpenIdConnectDefaults.AuthenticationScheme,
            nameType: "name",
            roleType: "role"));

        AuthenticationProperties properties = new();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "opaque-access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "opaque-refresh-token" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddMinutes(5).ToString("o", CultureInfo.InvariantCulture)
            }
        ]);

        CookieAuthenticationOptions cookieOptions = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        return cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(user, properties, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
