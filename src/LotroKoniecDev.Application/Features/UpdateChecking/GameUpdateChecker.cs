using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.UpdateChecking;

/// <summary>
/// Checks for LOTRO game updates by scraping the release notes forum.
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

    public Result ConfirmUpdateInstalled(
        string versionFilePath,
        string forumGameVersion,
        bool isFirstRun,
        DatVersionInfo previousDatVersion,
        DatVersionInfo currentDatVersion)
    {
        if (isFirstRun)
        {
            // First run — brak baseline'u, nie da się porównać vnum.
            // Gracz właśnie odpalił LOTRO launcher, ufamy że gra jest aktualna.
            return _gameVersionFileStore.SaveVersion(versionFilePath, forumGameVersion);
        }

        if (previousDatVersion == currentDatVersion)
        {
            // Vnum się nie zmienił — gracz mógł zamknąć launcher bez aktualizacji,
            // albo gra została zaktualizowana wcześniej (np. przez zwykły launcher).
            // Zapisujemy wersję forum i kontynuujemy — re-patch i tak zaaplikuje tłumaczenia.
            _logger.LogWarning(
                "DAT version unchanged after update flow (vnum={Vnum}). " +
                "Game may already be up to date. Saving forum version {ForumVersion} and continuing",
                currentDatVersion.VnumGameData, forumGameVersion);
        }

        return _gameVersionFileStore.SaveVersion(versionFilePath, forumGameVersion);
    }
    
    public async Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string gameVersionFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersionFilePath);

        Result<string?> storedVersionResult =
            _gameVersionFileStore.ReadLastKnownVersion(gameVersionFilePath);
        if (storedVersionResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckSummary>(storedVersionResult.Error);
        }

        string? storedVersion = storedVersionResult.Value;

        Result<string> fetchResult = await _forumPageFetcher.FetchReleaseNotesPageAsync();
        if (fetchResult.IsFailure)
        {
            _logger.LogWarning("Forum fetch failed: {Error}", fetchResult.Error.Message);
            return Result.Success(new GameUpdateCheckSummary(false, null, storedVersion));
        }

        string? forumVersion = ParseLatestVersion(fetchResult.Value);
        if (forumVersion is null)
        {
            _logger.LogWarning("Could not parse version from forum page");
            return Result.Success(new GameUpdateCheckSummary(false, null, storedVersion));
        }

        bool updateDetected = storedVersion is null || !string.Equals(
            forumVersion,
            storedVersion,
            StringComparison.OrdinalIgnoreCase);

        return Result.Success(new GameUpdateCheckSummary(updateDetected, forumVersion, storedVersion));
    }

    /// <summary>
    /// Extracts the latest game version number from forum page HTML.
    /// The first match is the latest because the forum lists threads in reverse chronological order.
    /// </summary>
    private static string? ParseLatestVersion(string htmlContent)
    {
        Match match = VersionRegex().Match(htmlContent);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
