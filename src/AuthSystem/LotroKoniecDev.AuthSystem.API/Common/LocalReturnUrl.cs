namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Returns the <c>returnUrl</c> only when it is a path on this site, otherwise <see langword="null"/>, so
/// the caller falls back to a safe default.
/// </summary>
/// <remarks>
/// Control characters are refused because a browser drops tab and newline: <c>"/\t/evil.example"</c>
/// becomes <c>"//evil.example"</c>. <c>LocalRedirect</c> would refuse such a target with a 500 after a
/// successful login. The <c>"~/"</c> form is refused too, because no auth page produces one.
/// Change it together with the frontend's twin, <c>Infrastructure/Security/LocalReturnUrl</c>.
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
