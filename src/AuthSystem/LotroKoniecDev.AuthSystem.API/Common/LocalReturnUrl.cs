namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The one check for every <c>returnUrl</c> a caller sends that the auth pages put into HTML or
/// redirect to. It returns the value only when it is a path on this site, and <see langword="null"/>
/// otherwise, so the caller falls back to a safe default.
/// </summary>
/// <remarks>
/// Control characters are rejected because the WHATWG URL parser drops ASCII tab and newline. So
/// <c>"/\t/evil.example"</c> becomes <c>"//evil.example"</c> once a browser parses it, and a check
/// that only looked at the first characters would call that value local.
/// Checking here keeps the value safe for the page to print, and it also spares
/// <c>LocalRedirect</c>, which refuses such a target and would turn a successful login into an
/// unhandled 500. The <c>"~/"</c> form is dropped too, because no auth page produces one.
/// The frontend has a twin in <c>Infrastructure/Security/LocalReturnUrl</c>. Change both together.
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
