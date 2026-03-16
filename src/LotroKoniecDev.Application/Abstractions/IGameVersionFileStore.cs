using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads and writes the locally stored game version (forum version + DAT vnums).
/// </summary>
public interface IGameVersionFileStore
{
    /// <summary>
    /// Reads the stored version info. Returns null if the file does not exist (first run).
    /// </summary>
    Result<StoredVersionInfo?> ReadStoredVersion(string versionFilePath);
    
    Result SaveVersion(
        string versionFilePath,
        string? forumVersion,
        int vnumDatFile,
        int vnumGameData,
        string? translationFileHash = null);
}
