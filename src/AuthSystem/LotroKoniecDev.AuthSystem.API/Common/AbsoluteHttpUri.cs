using System.Diagnostics.CodeAnalysis;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Shared by the settings validators, the frontend links and the CSP's form-action, so they never
/// disagree. The scheme check matters: on Unix a bare path such as <c>"/app"</c> parses as an absolute
/// <c>file://</c> URI.
/// </summary>
internal static class AbsoluteHttpUri
{
    internal static bool TryParse(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }
}
