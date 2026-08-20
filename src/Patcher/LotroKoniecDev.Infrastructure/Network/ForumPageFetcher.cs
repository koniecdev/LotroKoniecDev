using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Fetches the LOTRO release notes forum page over HTTP.
/// </summary>
public sealed class ForumPageFetcher : IForumPageFetcher
{
    /// <summary>
    /// The largest page we will read (AUDIT-SEC-04, #394). The release-notes page is HTML well under
    /// 1 MB, so 8 MiB leaves plenty of room while a hostile or broken server can no longer use up all
    /// our memory.
    /// </summary>
    public const long MaxResponseContentBytes = 8 * 1024 * 1024;

    private const string ReleaseNotesUrl =
        "https://forums.lotro.com/index.php?forums/release-notes-and-known-issues.7/";

    private readonly HttpClient _httpClient;

    public ForumPageFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<Result<string>> FetchReleaseNotesPageAsync()
    {
        try
        {
            // ResponseHeadersRead stops HttpClient from buffering the whole body before the size
            // limit is checked. It also takes the body read out of HttpClient.Timeout, so we apply
            // the same timeout around the whole fetch ourselves.
            using CancellationTokenSource timeoutCts = new(_httpClient.Timeout);

            using HttpResponseMessage response = await _httpClient.GetAsync(
                ReleaseNotesUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            Maybe<string> body = await BoundedResponseReader.TryReadAsStringAsync(
                response.Content, MaxResponseContentBytes, timeoutCts.Token);
            if (body.HasNoValue)
            {
                return Result.Failure<string>(
                    DomainErrors.GameUpdateCheck.ResponseTooLarge(MaxResponseContentBytes));
            }

            return Result.Success(body.Value);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<string>(
                DomainErrors.GameUpdateCheck.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<string>(
                DomainErrors.GameUpdateCheck.NetworkError("Request timed out."));
        }
    }
}
