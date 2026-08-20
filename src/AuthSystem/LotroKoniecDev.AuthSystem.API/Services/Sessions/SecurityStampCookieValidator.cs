using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The cookie <c>OnValidatePrincipal</c> handler. It checks the Identity security stamp on every
/// request to the auth server. When the stamp changes, which a password reset, change or delete does
/// through <see cref="UserManager{TUser}.UpdateSecurityStampAsync"/>, an auth cookie that is still
/// valid is rejected on its next request, so it can no longer finish <c>/connect/authorize</c> and get
/// fresh tokens.
/// That closes the window of up to 30 minutes left by SEC-02, which revokes the tokens a user already
/// has but not a live cookie's ability to create new ones. See ticket #282 (SEC-03).
/// </summary>
/// <remarks>
/// This is not the built-in <see cref="SecurityStampValidator.ValidatePrincipalAsync"/>, on purpose.
/// When that one rejects a cookie it always signs out
/// <see cref="IdentityConstants.TwoFactorRememberMeScheme"/>, which this auth server does not register:
/// it has one hardened <see cref="IdentityConstants.ApplicationScheme"/> cookie and no external-login
/// or two-factor schemes. That sign-out would throw and turn the intended redirect to the login page
/// into a 500.
/// This one checks the stamp through the same framework method the built-in validator uses,
/// <see cref="SignInManager{TUser}.ValidateSecurityStampAsync(System.Security.Claims.ClaimsPrincipal)"/>,
/// and signs out only the one scheme this server owns, like <c>LogoutEndpoint</c> does.
/// Checking on every request is deliberate: this is the auth server itself, so the interval is zero.
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
