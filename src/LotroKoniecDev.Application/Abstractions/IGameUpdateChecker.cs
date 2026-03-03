using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Checks if LOTRO has released a game update by comparing the forum version with a locally stored version.
/// </summary>
public interface IGameUpdateChecker
{
    public Result ConfirmUpdateInstalled(
        string datFilePath,
        string versionFilePath,
        string forumGameVersion,
        DatVersionInfo previousDatVersion);
    Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string gameVersionFilePath);
}

/// <summary>
/// Contains the result of a game update check.
/// </summary>
/// <param name="UpdateDetected">True if a new game version was found compared to the stored version.</param>
/// <param name="ForumVersion">The latest version string found on the LOTRO forums, or null if forum fetch failed.</param>
/// <param name="StoredVersion">The previously stored version string, or null if no version was stored (first run).</param>
public sealed record GameUpdateCheckSummary(
    bool UpdateDetected,
    string? ForumVersion,
    string? StoredVersion)
{
    public bool IsFirstLaunch => StoredVersion is null;
    public bool ForumCheckSucceeded => ForumVersion is not null;
}
