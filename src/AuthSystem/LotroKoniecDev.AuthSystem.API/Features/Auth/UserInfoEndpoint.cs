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

        // A client credentials token carries the client id as its subject, not a user id. Identity reads
        // a user id as a GUID and throws on anything else, so the lookup must not see it (#955).
        if (!Guid.TryParse(userId, out _))
        {
            return RefuseInvalidToken();
        }

        ApplicationUser? user = await userManager.FindByIdAsync(userId);

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
        endpointRouteBuilder.MapMethods("connect/userinfo", [HttpMethods.Get, HttpMethods.Post], HandleAsync)
            .RequireAuthorization();
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
