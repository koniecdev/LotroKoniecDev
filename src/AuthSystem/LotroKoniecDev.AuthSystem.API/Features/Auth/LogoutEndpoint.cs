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
/// Signing out ends every session of the user on every device, through the same revoker as a password
/// change (#931).
/// </summary>
internal sealed partial class LogoutEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IUserSessionRevoker sessionRevoker,
        ILogger<LogoutEndpoint> logger)
    {
        string? userId = await FindUserIdAsync(httpContext);
        if (!string.IsNullOrEmpty(userId))
        {
            await sessionRevoker.RevokeAllAsync(userId);
            LogUserLoggedOut(logger, userId);
        }

        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        OpenIddictRequest? request = httpContext.GetOpenIddictServerRequest();
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
    /// The hint comes first. After login the browser never comes back here, because the website renews
    /// its tokens server to server, so this server's own cookie is usually gone by the time the user
    /// signs out. OpenIddict has already checked the hint, and an invalid one gives no principal.
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
}
