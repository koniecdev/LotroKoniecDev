using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Reads and writes game version to a local text file.
/// </summary>
public sealed class GameVersionFileStore : IGameVersionFileStore
{
    public Result<string?> ReadLastKnownVersion(string versionFilePath)
    {
        try
        {
            if (!File.Exists(versionFilePath))
            {
                return Result.Success<string?>(null);
            }

            string content = File.ReadAllText(versionFilePath).Trim();
            return Result.Success<string?>(string.IsNullOrWhiteSpace(content) ? null : content);
        }
        catch (Exception ex)
        {
            return Result.Failure<string?>(DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, ex.Message));
        }
    }

    public Result SaveVersion(string versionFilePath, string forumGameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(forumGameVersion);
        try
        {
            string? directory = Path.GetDirectoryName(versionFilePath);

            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(versionFilePath, forumGameVersion);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, ex.Message));
        }
    }
}
