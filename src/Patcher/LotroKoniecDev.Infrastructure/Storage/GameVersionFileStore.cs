using System.Globalization;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Reads and writes the game version in a local text file.
/// The format is <c>forumVersion|vnumDatFile|vnumGameData</c>. Older files hold the forum version
/// alone, and those are still read.
/// </summary>
public sealed class GameVersionFileStore : IGameVersionFileStore
{
    private const char Separator = '|';

    public Result<StoredVersionInfo?> ReadStoredVersion(string versionFilePath)
    {
        try
        {
            if (!File.Exists(versionFilePath))
            {
                return Result.Success<StoredVersionInfo?>(null);
            }

            string content = File.ReadAllText(versionFilePath).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return Result.Success<StoredVersionInfo?>(null);
            }

            string[] parts = content.Split(Separator);

            if (parts.Length >= 3
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vnumDatFile)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vnumGameData))
            {
                string? forumVersion = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
                string? translationHash = parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3])
                    ? parts[3]
                    : null;
                return Result.Success<StoredVersionInfo?>(
                    new StoredVersionInfo(forumVersion, vnumDatFile, vnumGameData, translationHash));
            }

            // An older file: the forum version on its own, with no vnums.
            return Result.Success<StoredVersionInfo?>(new StoredVersionInfo(content, null, null));
        }
        catch (Exception ex)
        {
            return Result.Failure<StoredVersionInfo?>(DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, ex.Message));
        }
    }

    public Result SaveVersion(
        string versionFilePath,
        string? forumVersion,
        int vnumDatFile,
        int vnumGameData,
        string? translationFileHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);
        try
        {
            string? directory = Path.GetDirectoryName(versionFilePath);

            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string content = string.Create(CultureInfo.InvariantCulture,
                $"{forumVersion}{Separator}{vnumDatFile}{Separator}{vnumGameData}{Separator}{translationFileHash}");
            File.WriteAllText(versionFilePath, content);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, ex.Message));
        }
    }
}
