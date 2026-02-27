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

    public bool IsProtected(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        FileAttributes attributes = File.GetAttributes(datFilePath);
        return (attributes & FileAttributes.ReadOnly) != 0;
    }
}
