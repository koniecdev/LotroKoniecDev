using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using static OpenIddict.Abstractions.OpenIddictConstants;


namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Signing out revokes every token and authorization of the user, so the website sessions on all devices
/// end (#931). It does not change the security stamp, so this server's own cookie on another device
/// lives on until it expires.
/// </summary>
internal sealed partial class LogoutEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IUserSessionRevoker sessionRevoker,
        ILogger<LogoutEndpoint> logger)
    {
        OpenIddictRequest? request = httpContext.GetOpenIddictServerRequest();

        string? userId = await FindUserIdAsync(httpContext);
        if (!string.IsNullOrEmpty(userId))
        {
            await sessionRevoker.RevokeAllAsync(userId);
            LogUserLoggedOut(logger, userId);
        }
        else
        {
            LogSignOutFoundNoUser(logger, !string.IsNullOrEmpty(request?.IdTokenHint));
        }

        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        string? postLogoutRedirectUri = request?.PostLogoutRedirectUri;

        if (!string.IsNullOrEmpty(postLogoutRedirectUri))
        {
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = postLogoutRedirectUri },
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        return Results.SignOut(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// The hint comes first, because the website renews its tokens server to server and this server's
    /// own cookie is usually gone by sign-out time. OpenIddict checks the hint's signature and that its
    /// token and authorization rows are still valid. It does not check the lifetime or the client the
    /// hint was issued to, so any live ID token of the user works, and the hint of an ended session
    /// gives no principal.
    /// </summary>
    private static async Task<string?> FindUserIdAsync(HttpContext httpContext)
    {
        AuthenticateResult hintResult =
            await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        string? hintedUserId = hintResult.Principal?.GetClaim(Claims.Subject);
        if (!string.IsNullOrEmpty(hintedUserId))
        {
            return hintedUserId;
        }

        AuthenticateResult cookieResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        return cookieResult.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapMethods("connect/logout", [HttpMethods.Get, HttpMethods.Post], HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    [LoggerMessage(EventId = EventIds.UserLoggedOut, Level = LogLevel.Information, Message = "User logged out. UserId: {UserId}")]
    private static partial void LogUserLoggedOut(ILogger logger, string userId);

    [LoggerMessage(EventId = EventIds.SignOutFoundNoUser, Level = LogLevel.Information, Message = "Sign-out revoked nothing: there was no valid id_token_hint and no auth cookie. A hint was sent: {HintSent}")]
    private static partial void LogSignOutFoundNoUser(ILogger logger, bool hintSent);
}
