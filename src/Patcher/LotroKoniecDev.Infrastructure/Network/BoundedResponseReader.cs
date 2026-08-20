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

        return await content.ReadAsStringAsync(cancellationToken);
    }
}
