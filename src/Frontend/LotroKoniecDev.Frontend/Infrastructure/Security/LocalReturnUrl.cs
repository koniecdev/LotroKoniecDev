namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// The single guard for every caller-supplied return target the app redirects to — the login
/// challenge, the local sign-out and the cookie-consent bounce-back: yields the value only when it
/// is a same-site path, otherwise <see langword="null"/> so the caller falls back to a safe default.
/// </summary>
/// <remarks>
/// Control characters are rejected because the WHATWG URL parser strips ASCII tab and newline, so
/// <c>"/\t/evil.example"</c> would reach the browser as the protocol-relative
/// <c>"//evil.example"</c> — an open redirect a plain prefix check lets through.
/// Twin of the auth server's <c>Common/LocalReturnUrl</c> — change both together.
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
