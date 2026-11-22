namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

internal static class CookieConsentCookie
{
    internal const string Name = ".lotrokoniecdev.cookie-consent";
    internal const string Value = "true";

    // HttpOnly: only the server reads it. Secure follows the request scheme, so the cookie still works on
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

    internal static bool WasShown(string? cookieValue) => !string.IsNullOrWhiteSpace(cookieValue);
}
