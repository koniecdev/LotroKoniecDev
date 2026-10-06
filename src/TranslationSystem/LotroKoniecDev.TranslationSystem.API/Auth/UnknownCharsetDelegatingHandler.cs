using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// Drops the charset from an answer's <c>Content-Type</c> when .NET cannot decode by that name (#972).
/// .NET does not know some names, such as "utf8", and refuses others, such as "utf-7". The JWT bearer
/// handler reads the sign-in server's discovery document and signing keys with
/// <c>ReadAsStringAsync</c>, which throws on such a name, so every signed-in call failed. With no
/// charset, that read looks for a byte order mark and otherwise uses UTF-8, which is what JSON between
/// services is (RFC 8259 §8.1).
/// </summary>
internal sealed class UnknownCharsetDelegatingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // A header can name the charset more than once, and each pass removes one of them.
        while (response.Content.Headers.ContentType is { CharSet: { } charset } contentType && !CanDecodeBy(charset))
        {
            contentType.CharSet = null;
        }

        return response;
    }

    /// <summary>
    /// Looks the name up the way <c>ReadAsStringAsync</c> does: one pair of quotes off, then
    /// <see cref="Encoding.GetEncoding(string)"/>.
    /// </summary>
    private static bool CanDecodeBy(string charset)
    {
        string name = charset is ['"', _, .., '"'] ? charset[1..^1] : charset;
        try
        {
            Encoding.GetEncoding(name);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
