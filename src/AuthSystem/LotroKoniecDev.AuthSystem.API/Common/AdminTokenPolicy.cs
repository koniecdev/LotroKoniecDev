using Microsoft.AspNetCore.Authorization;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// A user's token that carries the admin role. A removed role stops working when the access token
/// expires, up to five minutes later (ADR-0049).
/// </summary>
internal static class AdminTokenPolicy
{
    public const string Name = "AdminToken";

    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder()
        .Combine(UserTokenPolicy.Policy)
        .RequireRole(AuthConstants.Roles.Admin)
        .Build();
}
