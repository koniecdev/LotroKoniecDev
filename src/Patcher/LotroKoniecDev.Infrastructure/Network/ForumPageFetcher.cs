using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Fetches the LOTRO release notes forum page over HTTP.
/// </summary>
public sealed class ForumPageFetcher : IForumPageFetcher
{
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
            using HttpResponseMessage response = await _httpClient.GetAsync(ReleaseNotesUrl);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();
            return Result.Success(content);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<string>(
                DomainErrors.GameUpdateCheck.NetworkError(ex.Message));
        }
        catch (TaskCanceledException)
        {
            return Result.Failure<string>(
                DomainErrors.GameUpdateCheck.NetworkError("Request timed out."));
        }
    }
}
