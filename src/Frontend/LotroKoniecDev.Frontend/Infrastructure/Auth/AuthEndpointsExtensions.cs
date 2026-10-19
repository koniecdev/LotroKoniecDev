using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Security;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Maps the login and logout routes the OIDC setup needs. Login starts the challenge; logout clears the
/// cookie, revokes the refresh token and ends the session at the auth server. The navbar's login and
/// logout buttons point here (M3-02).
/// </summary>
internal static class AuthEndpointsExtensions
{
    private const string EndSessionPath = "connect/logout";
    private const string IdTokenName = "id_token";
    private const string RefreshTokenName = "refresh_token";

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapAuthEndpoints()
        {
            // A page that needs a login sends the visitor here while the sign-in server is down, so this
            // route must never need a login itself, or that redirect would loop.
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
    /// The login route's handler, internal so a unit test can call it without a web host.
    /// While the sign-in server is down, the challenge cannot build its redirect, so the route answers 503
    /// and the status-code pages show the "login temporarily unavailable" page (#311). A page that needs a
    /// login sends the visitor here in that case, through <see cref="SignInOutageAuthorizationResultHandler"/>
    /// (#1153).
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
    /// A sign-out with no end-session round trip, for cases where the session at the auth server is already
    /// gone, for example right after an account deletion was scheduled and the auth server locked the
    /// account and revoked its tokens.
    /// The normal <see cref="LogoutAsync"/> goes through the OIDC end-session endpoint and always ends on
    /// the registered post-logout URI, the home page. This one skips that pointless round trip and ends
    /// on the given local page instead, the "deletion scheduled" info page.
    /// The auth server's own revoke is best effort, so the website still revokes its refresh token, like
    /// every other place where it ends a session (#1027).
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
    /// The logout route's handler, internal so a unit test can call it without a web host.
    /// The redirect below revokes this device's session only if the browser reaches the auth server. The
    /// refresh token belongs to the same session, so the website revokes it first, server to server. That
    /// revoke does not depend on the browser (#931, #964). The redirect stays for the auth server's own
    /// cookie.
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
