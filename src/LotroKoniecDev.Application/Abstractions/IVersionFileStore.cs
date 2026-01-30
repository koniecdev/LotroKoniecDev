using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads and writes the locally stored game version.
/// </summary>
public interface IVersionFileStore
{
    /// <summary>
    /// Reads the last known game version. Returns null if the file does not exist (first run).
    /// </summary>
    Result<string?> ReadLastKnownVersion(string filePath);

    /// <summary>
    /// Saves the game version string to the specified file.
    /// </summary>
    Result SaveVersion(string filePath, string version);
}
