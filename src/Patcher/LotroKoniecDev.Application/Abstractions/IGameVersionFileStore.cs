using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads and writes the locally stored game version (forum version + DAT vnums).
/// </summary>
public interface IGameVersionFileStore
{
    /// <summary>
    /// Returns null when the file does not exist yet, which is the case on the first run.
    /// </summary>
    Result<StoredVersionInfo?> ReadStoredVersion(string versionFilePath);

    Result SaveVersion(
        string versionFilePath,
        string? forumVersion,
        int vnumDatFile,
        int vnumGameData,
        string? translationFileHash = null);
}
