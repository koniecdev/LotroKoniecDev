namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The single guard for every caller-supplied <c>returnUrl</c> the auth pages reflect into HTML or
/// redirect to: yields the value only when it is a same-site path, otherwise <see langword="null"/>
/// so the caller falls back to a safe default.
/// </summary>
/// <remarks>
/// Control characters are rejected because the WHATWG URL parser strips ASCII tab and newline, so
/// <c>"/\t/evil.example"</c> reads as the protocol-relative <c>"//evil.example"</c> once a browser
/// parses it — a prefix-only check would call that value local. Screening here keeps the value safe
/// for the page to reflect and spares <c>LocalRedirect</c>'s executor, which rejects such a target
/// and would turn a successful login into an unhandled 500. The <c>"~/"</c> form is dropped too; no
/// auth page emits one. Twin of the frontend's <c>Infrastructure/Security/LocalReturnUrl</c> —
/// change both together.
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
