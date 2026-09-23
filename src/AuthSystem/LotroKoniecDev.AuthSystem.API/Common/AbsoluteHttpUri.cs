using System.Diagnostics.CodeAnalysis;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The one check for "an absolute http or https URL" that the settings validators, the frontend links
/// and the CSP's form-action share, so they can never disagree about what counts.
/// </summary>
/// <remarks>
/// The scheme check matters. On Unix a bare path such as <c>"/app"</c> parses as an absolute
/// <c>file://</c> URI.
/// </remarks>
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
