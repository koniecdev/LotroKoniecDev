using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Maps the login (challenge) and logout (cookie sign-out + RP-initiated end-session) endpoints the
/// OIDC RP wiring relies on. The navbar wires its login/logout controls to these in M3-02.
/// </summary>
internal static class AuthEndpointsExtensions
{
    private const string EndSessionPath = "connect/logout";
    private const string IdTokenName = "id_token";

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapAuthEndpoints()
        {
            endpoints.MapGet(
                AuthenticationDependencyInjectionExtensions.LoginPath,
                (HttpContext context, string? returnUrl) =>
                {
                    string redirectUri = IsLocalUrl(returnUrl) ? returnUrl! : "/";
                    return Results.Challenge(
                        new AuthenticationProperties { RedirectUri = redirectUri },
                        [OpenIdConnectDefaults.AuthenticationScheme]);
                });

            endpoints.MapPost(
                AuthenticationDependencyInjectionExtensions.LogoutPath,
                LogoutAsync);

            return endpoints;
        }
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptions<AuthSystemSettings> authSystemOptions)
    {
        string? idToken = await context.GetTokenAsync(IdTokenName);

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        string authority = authSystemOptions.Value.Authority.TrimEnd('/');
        string postLogoutRedirect = $"{context.Request.Scheme}://{context.Request.Host}";

        string endSessionUrl = $"{authority}/{EndSessionPath}" +
                               $"?post_logout_redirect_uri={Uri.EscapeDataString(postLogoutRedirect)}";

        if (!string.IsNullOrWhiteSpace(idToken))
        {
            endSessionUrl += $"&id_token_hint={Uri.EscapeDataString(idToken)}";
        }

        return Results.Redirect(endSessionUrl);
    }

    private static bool IsLocalUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
               && url.StartsWith('/')
               && !url.StartsWith("//", StringComparison.Ordinal)
               && !url.StartsWith("/\\", StringComparison.Ordinal);
    }
}
