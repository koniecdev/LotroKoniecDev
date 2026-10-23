using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Security;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

internal static class AuthEndpointsExtensions
{
    private const string EndSessionPath = "connect/logout";
    private const string IdTokenName = "id_token";
    private const string RefreshTokenName = "refresh_token";

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapAuthEndpoints()
        {
            // Every page that needs a login sends the visitor here, so a login here would loop.
            endpoints.MapGet(
                AuthenticationDependencyInjectionExtensions.LoginPath,
                LoginAsync)
                .AllowAnonymous();

            endpoints.MapPost(
                AuthenticationDependencyInjectionExtensions.LogoutPath,
                LogoutAsync);

            endpoints.MapPost(
                AuthenticationDependencyInjectionExtensions.LocalSignOutPath,
                LocalSignOutAsync);

            return endpoints;
        }
    }

    /// <summary>
    /// Internal so a unit test can call it without a web host. With no copy of the discovery document
    /// and the sign-in server down, the challenge cannot build its redirect, so the route answers 503 and
    /// the status-code pages show "login temporarily unavailable" (#311). With a copy, the visitor is
    /// still sent on (#1208).
    /// </summary>
    internal static async Task<IResult> LoginAsync(
        HttpContext context,
        string? returnUrl,
        SignInAvailability signInAvailability)
    {
        string redirectUri = LocalReturnUrl.Sanitize(returnUrl) ?? "/";

        if (!await signInAvailability.CanRedirectToSignInAsync(context.RequestAborted))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// A sign-out with no end-session round trip, for when the session at the auth server is already gone,
    /// such as right after an account deletion was scheduled. It ends on the given local page instead of
    /// the home page. The auth server's own revoke is best effort, so the website revokes its refresh
    /// token too (#1027).
    /// </summary>
    internal static async Task<IResult> LocalSignOutAsync(
        HttpContext context,
        string? returnUrl,
        RefreshTokenRevoker refreshTokenRevoker)
    {
        AuthenticateResult authentication =
            await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        string? refreshToken = authentication.Properties?.GetTokenValue(RefreshTokenName);

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await refreshTokenRevoker.RevokeAsync(refreshToken);

        string redirect = LocalReturnUrl.Sanitize(returnUrl) ?? "/";
        return Results.Redirect(redirect);
    }

    /// <summary>
    /// Internal so a unit test can call it without a web host. The end-session redirect works only if the
    /// browser reaches the auth server, so the website first revokes the refresh token server to server
    /// (#964). The redirect stays for the auth server's own cookie.
    /// </summary>
    internal static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptions<AuthSystemSettings> authSystemOptions,
        RefreshTokenRevoker refreshTokenRevoker)
    {
        AuthenticateResult authentication =
            await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        string? idToken = authentication.Properties?.GetTokenValue(IdTokenName);
        string? refreshToken = authentication.Properties?.GetTokenValue(RefreshTokenName);

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await refreshTokenRevoker.RevokeAsync(refreshToken);

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
}
