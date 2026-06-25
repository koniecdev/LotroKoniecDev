using System.Security.Cryptography;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Storage;

public sealed class FileHasher : IFileHasher
{
    public Result<string> ComputeHash(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            return Result.Success(Convert.ToHexStringLower(hash));
        }
        catch (Exception ex)
        {
            return Result.Failure<string>(
                DomainErrors.GameUpdateCheck.VersionFileError(filePath, ex.Message));
        }
    }
}
