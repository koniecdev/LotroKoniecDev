namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// The one check for every return target a caller sends that the app redirects to: the login challenge,
/// the local sign-out and the cookie-consent bounce-back. It returns the value only when it is a path on
/// this site, and <see langword="null"/> otherwise, so the caller falls back to a safe default.
/// </summary>
/// <remarks>
/// Control characters are rejected because the WHATWG URL parser drops ASCII tab and newline, so
/// <c>"/\t/evil.example"</c> would reach the browser as <c>"//evil.example"</c>, an open redirect a
/// check on the first characters alone would let through.
/// The auth server has a twin in <c>Common/LocalReturnUrl</c>. Change both together.
/// </remarks>
internal static class LocalReturnUrl
{
    internal static string? Sanitize(string? returnUrl) =>
        returnUrl is not null
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : null;
}
