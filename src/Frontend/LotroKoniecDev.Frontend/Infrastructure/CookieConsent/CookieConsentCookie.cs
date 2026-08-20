namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

internal static class CookieConsentCookie
{
    internal const string Name = ".lotrokoniecdev.cookie-consent";

    // The server decides whether to show the banner, from the request's cookie header. The app is pure
    // SSR and no script reads this cookie, and HttpOnly does not affect reads on the server.
    // Secure follows the request scheme, like the session-expired marker, so the cookie still works on
    // the plain-http dev profile. It carries nothing sensitive.
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
