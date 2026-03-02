using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// Reads and writes game version to a local text file.
/// </summary>
public sealed class VersionFileStore : IVersionFileStore
{
    public Result<string> ReadLastKnownVersion(string versionFilePath)
    {
        try
        {
            if (!File.Exists(versionFilePath))
            {
                return Result.Failure<string>(DomainErrors.GameUpdateCheck.ProgramIsLaunchedUpForTheFirstTime);
            }

            string content = File.ReadAllText(versionFilePath).Trim();
            return string.IsNullOrWhiteSpace(content) 
                ? Result.Failure<string>(
                    DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, "Version File is empty")) 
                : Result.Success(content);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>(DomainErrors.GameUpdateCheck.VersionFileError(versionFilePath, ex.Message));
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
