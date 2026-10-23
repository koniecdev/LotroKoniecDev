namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// The one check for every caller-sent return target the app redirects to. <see langword="null"/> when it
/// is not a path on this site.
/// </summary>
/// <remarks>
/// Control characters are rejected because the WHATWG URL parser drops tab and newline, so
/// <c>"/\t/evil.example"</c> would reach the browser as <c>"//evil.example"</c>, an open redirect.
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
