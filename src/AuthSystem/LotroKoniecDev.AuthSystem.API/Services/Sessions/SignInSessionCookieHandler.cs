using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Services.Sessions;

/// <summary>
/// The sign-in server's cookie handler (ADR-0062). Every sign-in gets a new session key, and a sign-out
/// always clears the browser's cookie.
/// </summary>
internal sealed partial class SignInSessionCookieHandler : CookieAuthenticationHandler
{
    /// <summary>
    /// The framework keeps this constant private, so it is repeated here; <c>SignInSessionTests</c> pins it
    /// against a real cookie.
    /// </summary>
    internal const string SessionKeyClaimType = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    public SignInSessionCookieHandler(
        IOptionsMonitor<CookieAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// The framework would renew the current cookie's session under its old key (aspnetcore#22135) and
    /// hand the new sign-in to every copy of that cookie. Deleting that session first makes it store a new
    /// one (ADR-0062).
    /// </summary>
    /// <remarks>
    /// If something earlier in the request had already read the cookie, the handler would keep the old
    /// key, which now names no session, so the user would have to sign in again. Nothing does that today.
    /// </remarks>
    protected override async Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        if (Options.SessionStore is { } sessionStore && ReadRequestSessionKey() is { } sessionKey)
        {
            await sessionStore.RemoveAsync(sessionKey, Context, CancellationToken.None);
        }

        await base.HandleSignInAsync(user, properties);
    }

    /// <summary>
    /// The framework clears the cookie only after the stored session is gone, so a database error would
    /// leave the browser signed in for the next person at a shared computer. Here it is cleared anyway.
    /// </summary>
    protected override async Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        try
        {
            await base.HandleSignOutAsync(properties);
        }
        catch (Exception exception)
        {
            LogStoredSessionNotEnded(Logger, exception);
            Options.CookieManager.DeleteCookie(Context, Options.Cookie.Name!, Options.Cookie.Build(Context));
        }
    }

    /// <summary>
    /// Opens the cookie the way the framework does, TLS token binding included, so the two always agree on
    /// which session it names.
    /// </summary>
    private string? ReadRequestSessionKey()
    {
        string? cookie = Options.CookieManager.GetRequestCookie(Context, Options.Cookie.Name!);
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        byte[]? tokenBindingId = Context.Features.Get<ITlsTokenBindingFeature>()?.GetProvidedTokenBindingId();
        string? tlsTokenBinding = tokenBindingId is null ? null : Convert.ToBase64String(tokenBindingId);

        return Options.TicketDataFormat.Unprotect(cookie, tlsTokenBinding)?.Principal.FindFirstValue(SessionKeyClaimType);
    }

    [LoggerMessage(EventId = EventIds.SignInSessionNotEndedAtSignOut, Level = LogLevel.Error, Message = "Signing out failed, so the stored sign-in session may still exist. The browser's cookie was cleared, but a copy of it can keep working until the session expires.")]
    private static partial void LogStoredSessionNotEnded(ILogger logger, Exception exception);
}
