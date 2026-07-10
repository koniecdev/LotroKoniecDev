using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Reads an HTTP response body as a string while enforcing a hard byte cap, so a hostile or
/// misbehaving server cannot exhaust process memory (AUDIT-SEC-04 / #394). A body whose declared
/// <c>Content-Length</c> exceeds the cap is rejected before any byte is transferred; a chunked or
/// lying response is cut off by the buffer limit while streaming. Callers must request the
/// response with <see cref="HttpCompletionOption.ResponseHeadersRead"/> — the default completion
/// option buffers the whole body inside <see cref="HttpClient"/> before any check here can run.
/// </summary>
internal static class BoundedResponseReader
{
    /// <summary>
    /// Returns the decoded body, or <see cref="Maybe{T}.None"/> when it exceeds
    /// <paramref name="maxResponseBytes"/>. Network failures keep surfacing as
    /// <see cref="HttpRequestException"/> for the caller's existing handling.
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
