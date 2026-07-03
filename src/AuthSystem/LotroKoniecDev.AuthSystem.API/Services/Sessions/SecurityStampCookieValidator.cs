using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Cookie <c>OnValidatePrincipal</c> handler that revalidates the Identity security stamp on every
/// request to the auth server. When a user's stamp rotates — password reset/change/delete call
/// <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/> — a still-live auth cookie is rejected on
/// its next request, so it can no longer complete <c>/connect/authorize</c> and mint fresh tokens.
/// This closes the ≤30-min re-mint window that survived SEC-02 (which revokes existing tokens but not a
/// live cookie's ability to create new ones). See ticket #282 (SEC-03).
/// </summary>
/// <remarks>
/// Deliberately NOT the built-in <see cref="SecurityStampValidator.ValidatePrincipalAsync"/>: its
/// rejection path unconditionally signs out <see cref="IdentityConstants.TwoFactorRememberMeScheme"/>,
/// which this à-la-carte auth server (a single hardened <see cref="IdentityConstants.ApplicationScheme"/>
/// cookie, no external-login/two-factor schemes) does not register — that sign-out would throw and turn
/// the intended redirect-to-login into a 500. This validates the stamp through the same framework
/// primitive the built-in validator uses (<see cref="SignInManager{TUser}.ValidateSecurityStampAsync(System.Security.Claims.ClaimsPrincipal)"/>)
/// and evicts only the one scheme this server owns, mirroring <c>LogoutEndpoint</c>. Validating on every
/// request is intentional — this is the auth origin (equivalent to a zero <c>ValidationInterval</c>).
/// </remarks>
internal static class SecurityStampCookieValidator
{
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        SignInManager<ApplicationUser> signInManager = context.HttpContext.RequestServices
            .GetRequiredService<SignInManager<ApplicationUser>>();

        ApplicationUser? user = await signInManager.ValidateSecurityStampAsync(context.Principal);
        if (user is not null)
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
