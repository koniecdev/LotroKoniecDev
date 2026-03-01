using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.DatFile;

public sealed class DatFileProtector : IDatFileProtector
{
    public Result Protect(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        try
        {
            FileAttributes attributes = File.GetAttributes(datFilePath);
            File.SetAttributes(datFilePath, attributes | FileAttributes.ReadOnly);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(DomainErrors.DatFileProtection.ProtectFailed(datFilePath, ex.Message));
        }
    }

    public Result Unprotect(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        try
        {
            FileAttributes attributes = File.GetAttributes(datFilePath);
            File.SetAttributes(datFilePath, attributes & ~FileAttributes.ReadOnly);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(DomainErrors.DatFileProtection.UnprotectFailed(datFilePath, ex.Message));
        }
    }

    public Result<bool> IsProtected(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        try
        {
            FileAttributes attributes = File.GetAttributes(datFilePath);
            bool result = (attributes & FileAttributes.ReadOnly) != 0;
            return Result.Success(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(DomainErrors.DatFileProtection.IsProtectedFailed(datFilePath, ex.Message));
        }
    }
}
