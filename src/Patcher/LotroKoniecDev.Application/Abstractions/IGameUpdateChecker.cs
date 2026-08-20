using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Tells whether LOTRO has published a game update, by reading the forum page and the version we
/// stored last time. It only reports and never saves. The handler decides what to do from this
/// summary and the DAT vnums.
/// </summary>
public interface IGameUpdateChecker
{
    Task<Result<GameUpdateCheckSummary>> CheckForUpdateAsync(string gameVersionFilePath);
}

/// <summary>
/// The result of an update check: the version on the forum and the version we stored. It holds no
/// decision. The handler compares the DAT vnums and decides.
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
