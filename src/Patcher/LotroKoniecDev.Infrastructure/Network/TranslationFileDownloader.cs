using System.Net;
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
            if (!string.IsNullOrEmpty(currentETag))
            {
                // The stored value is the server's quoted ETag; send it verbatim.
                request.Headers.TryAddWithoutValidation("If-None-Match", currentETag);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return Result.Success(TranslationFileFetchResult.NotModified());
            }

            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
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
        catch (TaskCanceledException)
        {
            return Result.Failure<TranslationFileFetchResult>(DomainErrors.TranslationFileSync.NetworkError("The request timed out."));
        }
    }
}
