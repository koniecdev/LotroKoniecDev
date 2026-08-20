using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.UpdateChecking;

/// <summary>
/// Reads the release notes forum to see whether LOTRO published an update. It only reports and never
/// saves the version.
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
            LogForumFetchFailed(_logger, fetchResult.Error.Message);
            return Result.Success(new GameUpdateCheckSummary(null, storedInfo));
        }

        string? forumVersion = ParseLatestVersion(fetchResult.Value);
        if (forumVersion is null)
        {
            LogForumVersionParseFailed(_logger);
            return Result.Success(new GameUpdateCheckSummary(null, storedInfo));
        }

        return Result.Success(new GameUpdateCheckSummary(forumVersion, storedInfo));
    }

    /// <summary>
    /// Reads the newest game version out of the forum page HTML. The first match is the newest,
    /// because the forum lists the latest threads first.
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
            // If the match times out on someone else's HTML we treat it as "could not parse". The
            // caller already copes with a null version.
            return null;
        }
    }

    // The pattern is linear, so it cannot blow up on input today. The timeout protects us from a
    // future edit to a regex that runs on someone else's HTML (AUDIT-SEC-07, #397).
    [GeneratedRegex(@"Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionRegex();

    [LoggerMessage(EventId = EventIds.ForumFetchFailed, Level = LogLevel.Warning, Message = "Forum fetch failed: {Error}")]
    private static partial void LogForumFetchFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.ForumVersionParseFailed, Level = LogLevel.Warning, Message = "Could not parse version from forum page")]
    private static partial void LogForumVersionParseFailed(ILogger logger);
}
