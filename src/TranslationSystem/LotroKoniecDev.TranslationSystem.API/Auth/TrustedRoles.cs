using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// The roles the admin gives by hand, so their holders are free of the limits meant for a fresh account:
/// the per-address request limit and the daily save budget (#1488). The admin can do all a reviewer can
/// (spec 0014), so it counts too.
/// </summary>
internal static class TrustedRoles
{
    public static bool AreHeldBy(ClaimsPrincipal user) =>
        user.IsInRole(AuthConstants.Roles.Reviewer) || user.IsInRole(AuthConstants.Roles.Admin);
}
