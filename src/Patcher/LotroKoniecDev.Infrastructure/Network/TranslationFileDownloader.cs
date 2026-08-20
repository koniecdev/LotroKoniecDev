using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Downloads the Polish translation file from the TMS over HTTP. It sends <c>If-None-Match</c>, so an
/// unchanged file comes back as a 304 instead of the whole body.
/// The endpoint arrives already resolved from the service document by rel (ADR-0041, #611): this type
/// knows no route, and neither does the rest of the patcher source.
/// A downloaded body is accepted only when its hash matches the server's ETag
/// (<see cref="TranslationFileContentIntegrity"/>). A damaged or altered file is refused here and the
/// sync falls back to the cached copy (AUDIT-SEC-01, #391).
/// </summary>
public sealed class TranslationFileDownloader : ITranslationFileDownloader
{
    /// <summary>
    /// The largest body we will download (AUDIT-SEC-04, #394). The full English export of the game's
    /// text is about 82 MB in the same <c>||</c> format, and a complete Polish file is about the same
    /// size, so 128 MiB leaves room to spare while a hostile or broken server can no longer use up all
    /// our memory.
    /// </summary>
    public const long MaxResponseContentBytes = 128 * 1024 * 1024;

    private readonly HttpClient _httpClient;

    public TranslationFileDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<Result<TranslationFileFetchResult>> FetchAsync(
        Uri endpoint, string? currentETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            // The stored value comes from a file on disk, so it goes through the typed header API and
            // not through TryAddWithoutValidation (AUDIT-SEC-07, #397). A value that no longer parses
            // as an ETag is dropped, and the fetch becomes a full download.
            if (!string.IsNullOrEmpty(currentETag)
                && EntityTagHeaderValue.TryParse(currentETag, out EntityTagHeaderValue? cachedETag))
            {
                request.Headers.IfNoneMatch.Add(cachedETag);
            }

            // ResponseHeadersRead stops HttpClient from buffering the whole body before the size
            // limit is checked. It also takes the body read out of HttpClient.Timeout, so we apply
            // the same timeout around the whole fetch with a linked token.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_httpClient.Timeout);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Result.Success(TranslationFileFetchResult.NotModified());
            }

            response.EnsureSuccessStatusCode();

            Maybe<string> body = await BoundedResponseReader.TryReadAsStringAsync(
                response.Content, MaxResponseContentBytes, timeoutCts.Token);
            if (body.HasNoValue)
            {
                return Result.Failure<TranslationFileFetchResult>(
                    DomainErrors.TranslationFileSync.ResponseTooLarge(MaxResponseContentBytes));
            }

            string content = body.Value;
            string eTag = response.Headers.ETag?.ToString() ?? string.Empty;

            if (!TranslationFileContentIntegrity.Matches(content, eTag))
            {
                return Result.Failure<TranslationFileFetchResult>(DomainErrors.TranslationFileSync.IntegrityCheckFailed(
                    "the response body does not match the server's ETag content hash."));
            }

            return Result.Success(TranslationFileFetchResult.Modified(content, eTag));
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<TranslationFileFetchResult>(DomainErrors.TranslationFileSync.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<TranslationFileFetchResult>(DomainErrors.TranslationFileSync.NetworkError("The request timed out."));
        }
    }
}
