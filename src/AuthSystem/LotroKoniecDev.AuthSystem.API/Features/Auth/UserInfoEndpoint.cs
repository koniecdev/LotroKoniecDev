using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed class UserInfoEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager)
    {
        ClaimsPrincipal principal = (await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!;

        string? userId = principal.GetClaim(Claims.Subject);

        // A client credentials token's subject is the client id, and Identity throws on a non-GUID id (#955).
        ApplicationUser? user = Guid.TryParse(userId, out _) ? await userManager.FindByIdAsync(userId) : null;

        if (user is null)
        {
            return RefuseInvalidToken();
        }

        Dictionary<string, object> claims = new(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id.ToString()
        };

        if (principal.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email!;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (principal.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = user.UserName!;
        }

        if (principal.HasScope(Scopes.Roles))
        {
            IList<string> roles = await userManager.GetRolesAsync(user);
            claims[Claims.Role] = roles;
        }

        return Results.Ok(claims);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // Any valid token that names this API, not UserTokenPolicy: a client's token must reach the
        // handler, which answers invalid_token, the error an OpenID Connect client reads here (#955).
        endpointRouteBuilder.MapMethods("connect/userinfo", [HttpMethods.Get, HttpMethods.Post], HandleAsync)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser());
    }

    private static IResult RefuseInvalidToken() =>
        Results.Challenge(
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is invalid."
            }),
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
