using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// Checks the security stamp on every request, so after a password, e-mail or deletion change a cookie
/// that is still valid can no longer finish <c>/connect/authorize</c> and get fresh tokens (#282). Token
/// revocation alone leaves a live cookie able to make new ones for up to 30 minutes.
/// </summary>
/// <remarks>
/// Not the built-in <see cref="SecurityStampValidator.ValidatePrincipalAsync"/>: on rejection it signs
/// out <see cref="IdentityConstants.TwoFactorRememberMeScheme"/>, which this server does not register,
/// so it would throw a 500. This one signs out only <see cref="IdentityConstants.ApplicationScheme"/>,
/// which also deletes the stored session (ADR-0062).
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
