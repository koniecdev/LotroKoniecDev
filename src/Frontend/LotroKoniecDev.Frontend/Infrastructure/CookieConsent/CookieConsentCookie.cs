namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

internal static class CookieConsentCookie
{
    internal const string Name = ".lotrokoniecdev.cookie-consent";

    // The show/hide decision is made server-side from the request cookie header — the app is pure
    // SSR with no client-side script reading this cookie, and HttpOnly does not affect server
    // reads. Secure mirrors the request scheme (like the session-expired marker) so the cookie
    // still round-trips on the plain-http dev profile; it carries no sensitive value.
    internal static CookieOptions BuildOptions(bool isHttps) => new()
    {
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        Path = "/"
    };

    internal static bool IsAccepted(string? cookieValue) => !string.IsNullOrWhiteSpace(cookieValue);
}
