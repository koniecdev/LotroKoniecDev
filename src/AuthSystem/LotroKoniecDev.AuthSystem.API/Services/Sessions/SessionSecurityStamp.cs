using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Ties an OpenIddict session to the security stamp, as <see cref="SecurityStampCookieValidator"/> does
/// for the cookie (#848): every code exchange and refresh compares it again, so a stamp change ends the
/// session even when token revocation failed.
/// </summary>
/// <remarks>
/// The claim must get no destination, so it stays in the code and the refresh token, which only this
/// server reads. Access tokens are not encrypted, and no client needs the stamp.
/// </remarks>
internal static class SessionSecurityStamp
{
    public const string ClaimType = "security_stamp";

    public static async Task AddAsync(
        ClaimsIdentity identity,
        ApplicationUser user,
        UserManager<ApplicationUser> userManager)
    {
        string securityStamp = await userManager.GetSecurityStampAsync(user);
        identity.SetClaim(ClaimType, securityStamp);
    }

    /// <summary>
    /// A principal with no stamp claim fails too, or an older token would be a way around the check.
    /// </summary>
    public static Task<bool> IsCurrentAsync(
        ClaimsPrincipal principal,
        ApplicationUser user,
        SignInManager<ApplicationUser> signInManager) =>
        signInManager.ValidateSecurityStampAsync(user, principal.GetClaim(ClaimType));
}
