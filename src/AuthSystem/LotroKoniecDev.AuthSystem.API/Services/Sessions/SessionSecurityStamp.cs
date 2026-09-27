using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Ties an OpenIddict session to the account's security stamp, the way
/// <see cref="SecurityStampCookieValidator"/> ties the auth cookie to it (#848). Every sign-in writes
/// the current stamp into the principal. Every code redemption and every refresh compares it with the
/// account's stamp again. So a flow that changes the stamp ends the session, even when the token
/// revocation failed or never ran.
/// </summary>
/// <remarks>
/// The claim must get no destination. OpenIddict then keeps it only in the authorization code and the
/// refresh token, which only this server can read. Access tokens are not encrypted, and Identity uses
/// the stamp as the key of its e-mail codes, so the stamp must never reach a token a client can read.
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
    /// A principal with no stamp claim fails too. That is a token issued before this check existed, and
    /// letting it through would leave a way around the check.
    /// </summary>
    public static Task<bool> IsCurrentAsync(
        ClaimsPrincipal principal,
        ApplicationUser user,
        SignInManager<ApplicationUser> signInManager) =>
        signInManager.ValidateSecurityStampAsync(user, principal.GetClaim(ClaimType));
}
