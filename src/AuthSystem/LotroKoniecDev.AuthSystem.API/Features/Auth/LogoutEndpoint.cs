using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Common;


namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class LogoutEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IOpenIddictTokenManager tokenManager,
        ILogger<LogoutEndpoint> logger)
    {
        AuthenticateResult cookieResult = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (cookieResult is { Succeeded: true })
        {
            string? userId = cookieResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId))
            {
                await foreach (object token in tokenManager.FindBySubjectAsync(userId))
                {
                    await tokenManager.TryRevokeAsync(token);
                }
            }

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

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapMethods("connect/logout", [HttpMethods.Get, HttpMethods.Post], HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    [LoggerMessage(EventId = EventIds.UserLoggedOut, Level = LogLevel.Information, Message = "User logged out. UserId: {UserId}")]
    private static partial void LogUserLoggedOut(ILogger logger, string? userId);
}
