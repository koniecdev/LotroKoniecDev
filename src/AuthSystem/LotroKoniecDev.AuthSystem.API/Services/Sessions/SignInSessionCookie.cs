using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The one way to sign a user in to the sign-in server: every sign-in gets a session of its own
/// (ADR-0062).
/// </summary>
internal static class SignInSessionCookie
{
    /// <summary>
    /// The claim the cookie handler writes the session key under. ASP.NET Core keeps its own constant
    /// private, so it is repeated here, and <c>SignInSessionTests</c> pins it against a real cookie.
    /// </summary>
    internal const string SessionKeyClaimType = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    /// <summary>
    /// Ends the session that the browser's current cookie names, then signs in. Before it signs in, the
    /// cookie handler reads the cookie the browser already has, and while that cookie's session lives,
    /// it puts the new sign-in under the old key. Every copy of the old cookie would then hold the new
    /// sign-in. A copy that a password change had killed would work again, and on a shared computer a
    /// copy of another user's cookie would be signed in as this user. Once the old session is gone, the
    /// handler stores the new sign-in under a new key.
    /// </summary>
    /// <remarks>
    /// Call it before anything else in the request reads this cookie. The handler reads the cookie once
    /// and keeps the key it found.
    /// </remarks>
    public static async Task SignInAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        AuthenticationProperties properties)
    {
        CookieAuthenticationOptions options = httpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        if (options.SessionStore is { } sessionStore
            && options.CookieManager.GetRequestCookie(httpContext, options.Cookie.Name!) is { Length: > 0 } cookie
            && options.TicketDataFormat.Unprotect(cookie)?.Principal.FindFirstValue(SessionKeyClaimType) is { } sessionKey)
        {
            await sessionStore.RemoveAsync(sessionKey, httpContext, CancellationToken.None);
        }

        await httpContext.SignInAsync(IdentityConstants.ApplicationScheme, principal, properties);
    }
}
