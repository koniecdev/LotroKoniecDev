using System.Text;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// Drops a charset .NET cannot decode by ("utf8", "utf-7") from <c>Content-Type</c> (#972). The OIDC
/// handler reads the sign-in server's answers with <c>ReadAsStringAsync</c>, which throws on such a name.
/// With no charset it uses UTF-8, which is what JSON between services is (RFC 8259 §8.1). Our own clients
/// read the bytes as UTF-8 and do not need this.
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
