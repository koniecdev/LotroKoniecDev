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
    private readonly IVersionFileStore _versionFileStore;

    public GameUpdateChecker(
        IForumPageFetcher forumPageFetcher,
        IDatVersionReader datVersionReader,
        IVersionFileStore versionFileStore)
    {
        _forumPageFetcher = forumPageFetcher;
        _datVersionReader = datVersionReader;
        _versionFileStore = versionFileStore;
    }

    public Result ConfirmUpdateInstalled(
        string datFilePath,
        string versionFilePath,
        string forumVersion,
        DatVersionInfo previousDatVersion)
    {
        Result<DatVersionInfo> currentDatVersionResult = _datVersionReader.ReadVersion(datFilePath);
        if (currentDatVersionResult.IsFailure)
        {
            return Result.Failure(currentDatVersionResult.Error);
        }

        return previousDatVersion == currentDatVersionResult.Value 
            ? Result.Failure(DomainErrors.GameUpdateCheck.GameUpdateRequired) 
            : Result.Success();

        //musimy alertować usera, odpalaj gre, aktualizuj, i albo zamknij
        //launcher lotro, albo szukamy procesu gry, i mu ją terminujemy razem z launcherem,
        //wtedy resume tutaj.
        //resume tutaj - czyli re-read vnum, jak sie zmienily, to znaczy ze faktycznie
        //user odaplil gre, czyli jest aktualna.
        //jezeli zamknal lotro launcher przed update to znaczy ze zamknal za wczesnie
        //launcher. 
        //jak sie rozni vnum, zapisujemy wersje z forum jako aktualna
        //jezeli sie nie rozni, to mowimy mu halo, coś tu nie gra, odpal ten launcher
        //mozna przemyslec na przyszlosc czy dac opcje continue anyway i zapisac po prostu najnwosza wersje
        //risky ale co najwyzej raz odpali lotro, zesra sie ze nie dziala mu tluamczneie,
        //wejdzie ponownie i zadziala najpewniej.
        
        //powyzsze realizuje 
        // public async Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string versionFilePath)
        // {
        //     ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);
        //
        //     Result<string> locallySavedLastKnownVersionResult = _versionFileStore.ReadLastKnownVersion(versionFilePath); <--
        
        //handler musi to wywołać, z pattern matchingowac failure na DomainErrors.GameUpdateCheck.ProgramIsLaunchedUpForTheFirstTime,
        //i dopiero wtedy ten caly powyzszy flow.
        //generalnie - handler do scraftowania teraz
    }
    
    public async Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string versionFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);

        Result<string> locallySavedLastKnownVersionResult = _versionFileStore.ReadLastKnownVersion(versionFilePath);
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
