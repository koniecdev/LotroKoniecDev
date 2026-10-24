using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The default policy: a valid token issued to a user, because every signed-in endpoint here acts on the
/// caller's own account. A service token's subject is the client id. Today the audience check already
/// refuses it with 401 (#1023); this policy is the second layer and answers 403 (#966). An endpoint that
/// takes any valid token says so with its own policy, as <c>UserInfoEndpoint</c> does.
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
