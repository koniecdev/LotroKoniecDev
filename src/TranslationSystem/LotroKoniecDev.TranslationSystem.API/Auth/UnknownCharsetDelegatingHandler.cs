using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// Drops a charset .NET cannot decode by, such as "utf8" or "utf-7" (#972). IdentityModel reads the
/// discovery document and signing keys with <c>ReadAsStringAsync</c>, which throws on such a name, so
/// every signed-in call failed. With no charset it falls back to UTF-8, which JSON is (RFC 8259 §8.1).
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
    /// The way <c>ReadAsStringAsync</c> looks it up: one pair of quotes off, then
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
