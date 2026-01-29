using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure;

/// <summary>
/// Reads and writes game version to a local text file.
/// </summary>
public sealed class VersionFileStore : IVersionFileStore
{
    public Result<string?> ReadLastKnownVersion(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Result.Success<string?>(null);
            }

            string content = File.ReadAllText(filePath).Trim();
            return Result.Success<string?>(string.IsNullOrWhiteSpace(content) ? null : content);
        }
        catch (Exception ex)
        {
            return Result.Failure<string?>(
                DomainErrors.GameUpdateCheck.VersionFileError(filePath, ex.Message));
        }
    }

    public Result SaveVersion(string filePath, string version)
    {
        try
        {
            string? directory = Path.GetDirectoryName(filePath);

            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, version);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.GameUpdateCheck.VersionFileError(filePath, ex.Message));
        }
    }
}
