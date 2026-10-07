using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The default policy of this API: a valid token issued to a user. Every endpoint here that needs a
/// sign-in acts on the caller's own account. A service token's subject is the client id and not a user id
/// (<c>TokenEndpoint.HandleClientCredentialsGrant</c>). Today it does not name this API, so the audience
/// check refuses it with 401 before this policy runs (#1023). The policy is the second layer: a token
/// issued to a client that does name this API gets the 403 a failed policy gives, before any handler
/// runs (#966). An endpoint that takes any valid token says so with its own policy, as
/// <c>UserInfoEndpoint</c> does.
/// </summary>
internal static class UserTokenPolicy
{
    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => IsIssuedToUser(context.User))
        .Build();

    private static bool IsIssuedToUser(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindUserId(), out _);
}
