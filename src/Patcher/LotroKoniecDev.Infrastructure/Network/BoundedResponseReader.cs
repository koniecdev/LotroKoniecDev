using System.Text;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Reads an HTTP response body as a string but never past a fixed byte limit, so a hostile or broken
/// server cannot use up all our memory (AUDIT-SEC-04, #394). A body whose <c>Content-Length</c> is
/// over the limit is refused before a single byte is transferred, and a chunked response, or one that
/// lies about its length, is cut off while streaming.
/// Callers must ask for the response with <see cref="HttpCompletionOption.ResponseHeadersRead"/>. The
/// default option makes <see cref="HttpClient"/> buffer the whole body before any check here runs.
/// </summary>
internal static class BoundedResponseReader
{
    /// <summary>
    /// Returns the decoded body, or <see cref="Maybe{T}.None"/> when it is larger than
    /// <paramref name="maxResponseBytes"/>. Network failures still come out as
    /// <see cref="HttpRequestException"/>, so callers handle them as before.
    /// The body is decoded by the charset in <c>Content-Type</c> when .NET can use that name, because
    /// the forum page is HTML and a real charset matters there. A name .NET does not know, such as
    /// "utf8", or refuses, such as "utf-7", is read as UTF-8 instead of throwing up to the launch
    /// command (#972). For the translation file that is the same read as under "utf-8", so the ETag
    /// hash check sees the same text.
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
