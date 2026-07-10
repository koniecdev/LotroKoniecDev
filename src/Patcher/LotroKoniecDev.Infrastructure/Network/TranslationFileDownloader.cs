using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Downloads the Polish translation file from the TMS distribution endpoint over HTTP with a
/// conditional <c>If-None-Match</c> request, so an unchanged file returns 304 rather than its bytes.
/// A downloaded body is accepted only when it hash-matches the server's ETag
/// (<see cref="TranslationFileContentIntegrity"/>) — a corrupted or tampered file is rejected here,
/// and the sync falls back to the cached copy (AUDIT-SEC-01 / #391).
/// </summary>
public sealed class TranslationFileDownloader : ITranslationFileDownloader
{
    /// <summary>
    /// Hard cap on the downloaded body size (AUDIT-SEC-04 / #394). The full English export of the
    /// game's text corpus measures ~82 MB in the same <c>||</c> format, and a complete Polish
    /// translation file is the same order of magnitude, so 128 MiB leaves comfortable headroom
    /// while a hostile or misbehaving server can no longer exhaust process memory.
    /// </summary>
    public const long MaxResponseContentBytes = 128 * 1024 * 1024;

    private const string TranslationFileRoute = "api/v1/translation-files/pl";

    private readonly HttpClient _httpClient;

    public TranslationFileDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<Result<TranslationFileFetchResult>> FetchAsync(
        string baseUrl, string? currentETag, CancellationToken cancellationToken)
    {
        try
        {
            Uri requestUri = new($"{baseUrl.TrimEnd('/')}/{TranslationFileRoute}", UriKind.Absolute);
            using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
            // The stored value comes from an on-disk sidecar file, so it goes through the typed
            // header API instead of TryAddWithoutValidation (AUDIT-SEC-07 / #397). A value that no
            // longer parses as an ETag is simply dropped — the fetch degrades to a full download.
            if (!string.IsNullOrEmpty(currentETag)
                && EntityTagHeaderValue.TryParse(currentETag, out EntityTagHeaderValue? cachedETag))
            {
                request.Headers.IfNoneMatch.Add(cachedETag);
            }

            // ResponseHeadersRead keeps HttpClient from buffering the whole body before the size
            // cap can run, which also moves the body read out of HttpClient.Timeout's scope — so
            // the timeout is re-applied around the entire fetch via a linked token.
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
