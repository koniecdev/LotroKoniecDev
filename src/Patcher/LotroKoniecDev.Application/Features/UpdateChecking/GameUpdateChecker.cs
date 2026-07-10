using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.UpdateChecking;

/// <summary>
/// Checks for LOTRO game updates by scraping the release notes forum.
/// Reports only — never saves version data.
/// </summary>
public sealed partial class GameUpdateChecker : IGameUpdateChecker
{
    private readonly IForumPageFetcher _forumPageFetcher;
    private readonly IGameVersionFileStore _gameVersionFileStore;
    private readonly ILogger<GameUpdateChecker> _logger;

    public GameUpdateChecker(
        IForumPageFetcher forumPageFetcher,
        IGameVersionFileStore gameVersionFileStore,
        ILogger<GameUpdateChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(forumPageFetcher);
        ArgumentNullException.ThrowIfNull(gameVersionFileStore);
        ArgumentNullException.ThrowIfNull(logger);

        _forumPageFetcher = forumPageFetcher;
        _gameVersionFileStore = gameVersionFileStore;
        _logger = logger;
    }

    public async Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string gameVersionFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersionFilePath);

        Result<StoredVersionInfo?> storedResult =
            _gameVersionFileStore.ReadStoredVersion(gameVersionFilePath);
        if (storedResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckSummary>(storedResult.Error);
        }

        StoredVersionInfo? storedInfo = storedResult.Value;

        Result<string> fetchResult = await _forumPageFetcher.FetchReleaseNotesPageAsync();
        if (fetchResult.IsFailure)
        {
            _logger.LogWarning("Forum fetch failed: {Error}", fetchResult.Error.Message);
            return Result.Success(new GameUpdateCheckSummary(null, storedInfo));
        }

        string? forumVersion = ParseLatestVersion(fetchResult.Value);
        if (forumVersion is null)
        {
            _logger.LogWarning("Could not parse version from forum page");
            return Result.Success(new GameUpdateCheckSummary(null, storedInfo));
        }

        return Result.Success(new GameUpdateCheckSummary(forumVersion, storedInfo));
    }

    /// <summary>
    /// Extracts the latest game version number from forum page HTML.
    /// The first match is the latest because the forum lists threads in reverse chronological order.
    /// </summary>
    private static string? ParseLatestVersion(string htmlContent)
    {
        try
        {
            Match match = VersionRegex().Match(htmlContent);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // A timed-out match on third-party HTML is treated as "could not parse" — the caller
            // already handles a null version gracefully.
            return null;
        }
    }

    // The pattern is linear, so it is not ReDoS-prone today; the timeout is a guardrail against
    // future edits to a regex that runs on third-party HTML (AUDIT-SEC-07 / #397).
    [GeneratedRegex(@"Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionRegex();
}
