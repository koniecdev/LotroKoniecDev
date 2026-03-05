using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Checks if LOTRO has released a game update by scraping the forum and reading the stored version.
/// Reports only — never saves. The handler decides what to do based on the summary + DAT vnums.
/// </summary>
public interface IGameUpdateChecker
{
    Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string gameVersionFilePath);
}

/// <summary>
/// Contains the result of a game update check: forum version + stored version info.
/// Does NOT contain an update decision — the handler compares DAT vnums to decide.
/// </summary>
public sealed record GameUpdateCheckSummary(
    string? ForumVersion,
    StoredVersionInfo? StoredInfo)
{
    public bool IsFirstLaunch => StoredInfo is null;
    public bool ForumCheckSucceeded => ForumVersion is not null;

    public bool ForumVersionChanged =>
        StoredInfo is not null
        && ForumVersion is not null
        && !string.Equals(ForumVersion, StoredInfo.ForumVersion, StringComparison.OrdinalIgnoreCase);
}
