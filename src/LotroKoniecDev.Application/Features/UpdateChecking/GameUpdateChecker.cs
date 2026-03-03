using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
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
    private readonly IDatVersionReader _datVersionReader;
    private readonly IGameVersionFileStore _gameVersionFileStore;
    private readonly ILogger<GameUpdateChecker> _logger;

    public GameUpdateChecker(
        IForumPageFetcher forumPageFetcher,
        IDatVersionReader datVersionReader,
        IGameVersionFileStore gameVersionFileStore,
        ILogger<GameUpdateChecker> logger)
    {
        _forumPageFetcher = forumPageFetcher;
        _datVersionReader = datVersionReader;
        _gameVersionFileStore = gameVersionFileStore;
        _logger = logger;
    }

    public Result ConfirmUpdateInstalled(
        string datFilePath,
        string versionFilePath,
        string forumGameVersion,
        DatVersionInfo previousDatVersion)
    {
        Result<string?> storedVersion = _gameVersionFileStore.ReadLastKnownVersion(versionFilePath);
        if (storedVersion.IsFailure)
        {
            return Result.Failure(storedVersion.Error);
        }

        bool isFirstRun = storedVersion.Value is null;

        // First run — brak baseline'u, nie da się porównać vnum.
        // Gracz właśnie odpalił LOTRO launcher, ufamy że gra jest aktualna.
        if (isFirstRun)
        {
            return _gameVersionFileStore.SaveVersion(versionFilePath, forumGameVersion);
        }

        // Kolejne uruchomienia — weryfikujemy że vnum się zmienił (czyli update zainstalowany)
        Result<DatVersionInfo> currentDatVersionResult = _datVersionReader.ReadVersion(datFilePath);
        if (currentDatVersionResult.IsFailure)
        {
            return Result.Failure(currentDatVersionResult.Error);
        }

        if (previousDatVersion == currentDatVersionResult.Value)
        {
            return Result.Failure(DomainErrors.GameUpdateCheck.GameUpdateRequired);
        }
        
        Result saveGameVersionResult = _gameVersionFileStore.SaveVersion(versionFilePath, forumGameVersion);
        return saveGameVersionResult;
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
