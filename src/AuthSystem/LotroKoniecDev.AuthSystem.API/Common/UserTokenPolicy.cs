using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The default policy of this API: a valid token issued to a user. Every endpoint here that needs a
/// sign-in acts on the caller's own account. A service token is valid too, but its subject is the client
/// id and not a user id (<c>TokenEndpoint.HandleClientCredentialsGrant</c>). So it gets the 403 a failed
/// policy gives, before any handler runs (#966). An endpoint that takes any valid token says so with its
/// own policy, as <c>UserInfoEndpoint</c> does.
/// </summary>
internal static class UserTokenPolicy
{
    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => IsIssuedToUser(context.User))
        .Build();

    // The user id is a GUID. This reads it the way the account endpoints do, so the policy and the
    // handler always look at the same claim.
    private static bool IsIssuedToUser(ClaimsPrincipal principal) =>
        Guid.TryParse(
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(Claims.Subject),
            out _);
}
