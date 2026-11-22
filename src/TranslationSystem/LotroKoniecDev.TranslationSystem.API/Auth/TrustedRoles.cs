using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// The roles whose holders have no daily save budget (#1488, ADR-0072). The admin can do all a reviewer
/// can (spec 0014), so it counts too.
/// </summary>
internal static class TrustedRoles
{
    public static bool AreHeldBy(ClaimsPrincipal user) =>
        user.IsInRole(AuthConstants.Roles.Reviewer) || user.IsInRole(AuthConstants.Roles.Admin);
}
