using System.Text;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Reads an HTTP response body as a string, never past a byte limit, so a hostile or broken server
/// cannot use up our memory (#394). A <c>Content-Length</c> over the limit is refused before any byte
/// moves; a chunked body, or one that lies about its length, is cut off while streaming.
/// Callers must ask for the response with <see cref="HttpCompletionOption.ResponseHeadersRead"/>: the
/// default makes <see cref="HttpClient"/> buffer the whole body before any check here runs.
/// </summary>
internal static class BoundedResponseReader
{
    /// <summary>
    /// Returns the decoded body, or <see cref="Maybe{T}.None"/> when it is larger than
    /// <paramref name="maxResponseBytes"/>. Network failures still throw <see cref="HttpRequestException"/>.
    /// The body is decoded by the <c>Content-Type</c> charset when .NET can use that name, because the
    /// forum page is HTML. A name .NET does not know ("utf8") or refuses ("utf-7") is read as UTF-8
    /// instead of throwing up to the launch command (#972). For the translation file that is the same
    /// read as "utf-8", so the ETag hash check sees the same text.
    /// </summary>
    internal static async Task<Maybe<string>> TryReadAsStringAsync(
        HttpContent content, long maxResponseBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxResponseBytes)
        {
            return Maybe<string>.None;
        }

        try
        {
            await content.LoadIntoBufferAsync(maxResponseBytes, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return Maybe<string>.None;
        }

        if (!CanDecodeBy(content.Headers.ContentType?.CharSet))
        {
            byte[] body = await content.ReadAsByteArrayAsync(cancellationToken);
            int bomLength = body.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            return Encoding.UTF8.GetString(body, bomLength, body.Length - bomLength);
        }

        return await content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Looks the name up the way <see cref="HttpContent.ReadAsStringAsync()"/> does: one pair of quotes
    /// off, then <see cref="Encoding.GetEncoding(string)"/>. No name means no lookup, so that read sniffs
    /// a byte order mark and falls back to UTF-8 on its own.
    /// </summary>
    private static bool CanDecodeBy(string? charset)
    {
        if (charset is null)
        {
            return true;
        }

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
