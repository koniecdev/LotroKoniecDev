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
/// Signing out ends the session of this device only: the one authorization behind the
/// <c>id_token_hint</c> and its tokens (owner decision on #931). Sessions on other devices stay alive.
/// </summary>
internal sealed partial class LogoutEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IUserSessionRevoker sessionRevoker,
        ILogger<LogoutEndpoint> logger)
    {
        OpenIddictRequest? request = httpContext.GetOpenIddictServerRequest();

        // OpenIddict checks the hint's signature and that its token and authorization rows are still
        // valid, but not its lifetime. So an old ID token works while its session lives, and the hint of
        // an ended session gives no principal. This server's own cookie cannot name the website's
        // session: it belongs to the browser, not to one sign-in of the website. Signing it out deletes its
        // stored session as well, so a copy of the cookie stops working too (ADR-0062).
        AuthenticateResult hintResult =
            await httpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (hintResult.Principal is { } principal
            && principal.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            await sessionRevoker.RevokeSessionAsync(authorizationId);
            LogUserLoggedOut(logger, principal.GetClaim(Claims.Subject));
        }
        else
        {
            LogSignOutFoundNoSession(logger, !string.IsNullOrEmpty(request?.IdTokenHint));
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

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapMethods("connect/logout", [HttpMethods.Get, HttpMethods.Post], HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    [LoggerMessage(EventId = EventIds.UserLoggedOut, Level = LogLevel.Information, Message = "User logged out. UserId: {UserId}")]
    private static partial void LogUserLoggedOut(ILogger logger, string? userId);

    [LoggerMessage(EventId = EventIds.SignOutFoundNoSession, Level = LogLevel.Information, Message = "Sign-out revoked nothing: there was no valid id_token_hint. A hint was sent: {HintSent}")]
    private static partial void LogSignOutFoundNoSession(ILogger logger, bool hintSent);
}
