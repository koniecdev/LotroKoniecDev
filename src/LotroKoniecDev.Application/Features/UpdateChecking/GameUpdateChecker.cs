using System.Text.RegularExpressions;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Features.UpdateChecking;

/// <summary>
/// Checks for LOTRO game updates by scraping the release notes forum.
/// </summary>
public sealed partial class GameUpdateChecker : IGameUpdateChecker
{
    private readonly IForumPageFetcher _forumPageFetcher;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IGameVersionFileStore _gameVersionFileStore;

    public GameUpdateChecker(
        IForumPageFetcher forumPageFetcher,
        IDatVersionReader datVersionReader,
        IGameVersionFileStore gameVersionFileStore)
    {
        _forumPageFetcher = forumPageFetcher;
        _datVersionReader = datVersionReader;
        _gameVersionFileStore = gameVersionFileStore;
    }

    public Result ConfirmUpdateInstalled(
        string datFilePath,
        string versionFilePath,
        string forumGameVersion,
        DatVersionInfo previousDatVersion)
    {
        Result<DatVersionInfo> currentDatVersionResult = _datVersionReader.ReadVersion(datFilePath);
        if (currentDatVersionResult.IsFailure)
        {
            return Result.Failure(currentDatVersionResult.Error);
        }
        
        DatVersionInfo currentDatVersion = currentDatVersionResult.Value;

        if (previousDatVersion == currentDatVersion)
        {
            return Result.Failure(DomainErrors.GameUpdateCheck.GameUpdateRequired);
        }
        
        Result saveGameVersionResult = _gameVersionFileStore.SaveVersion(versionFilePath, forumGameVersion);
        return saveGameVersionResult;
    }
    
    public async Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string versionFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);

        Result<string> locallySavedLastKnownVersionResult = _gameVersionFileStore.ReadLastKnownVersion(versionFilePath);
        if (locallySavedLastKnownVersionResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckSummary>(locallySavedLastKnownVersionResult.Error);
        }
        
        string locallySavedLastKnownVersion = locallySavedLastKnownVersionResult.Value;
        
        Result<string> releaseNotesPageVersionResult = await _forumPageFetcher.FetchReleaseNotesPageAsync();
        if (releaseNotesPageVersionResult.IsFailure)
        {
            return Result.Failure<GameUpdateCheckSummary>(releaseNotesPageVersionResult.Error);
        }

        string? parsedReleaseNotesPageVersion = ParseLatestVersion(releaseNotesPageVersionResult.Value);
        if (parsedReleaseNotesPageVersion is null)
        {
            return Result.Failure<GameUpdateCheckSummary>(DomainErrors.GameUpdateCheck.VersionNotFoundInPage);
        }

        bool updateDetected = !string.Equals(
            parsedReleaseNotesPageVersion, 
            locallySavedLastKnownVersion,
            StringComparison.OrdinalIgnoreCase);

        GameUpdateCheckSummary result = new(updateDetected, parsedReleaseNotesPageVersion, locallySavedLastKnownVersion);
        return Result.Success(result);
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
