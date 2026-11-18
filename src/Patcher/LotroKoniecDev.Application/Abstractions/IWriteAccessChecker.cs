using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// What a read-write open of one file said. The default value means the file can be written, so a
/// test substitute that nobody set up answers "writable".
/// </summary>
/// <param name="OsReason">The system's own words for the refusal.</param>
public readonly record struct DatFileWriteCheck(DatFileWriteRefusal Refusal, string? OsReason = null)
{
    /// <summary>
    /// Turns the refusal into an error that tells the player what to do. The native open of the DAT
    /// gives no reason, so this is the only place one comes from (#1248).
    /// </summary>
    public Result ToResult(string datFilePath)
    {
        if (Refusal == DatFileWriteRefusal.None)
        {
            return Result.Success();
        }

        string fullPath = Path.GetFullPath(datFilePath);

        return Result.Failure(Refusal switch
        {
            DatFileWriteRefusal.ReadOnly => DomainErrors.DatFile.ReadOnly(fullPath),
            DatFileWriteRefusal.AccessDenied => DomainErrors.DatFile.WriteAccessDenied(fullPath),
            DatFileWriteRefusal.InUse => DomainErrors.DatFile.InUse(fullPath),
            DatFileWriteRefusal.NotFound => DomainErrors.DatFile.NotFound(fullPath),
            _ => DomainErrors.DatFile.CannotOpenForWriting(fullPath, OsReason)
        });
    }
}

public interface IWriteAccessChecker
{
    bool CanWriteTo(string directoryPath);

    /// <summary>
    /// Opens <paramref name="filePath"/> for reading and writing, closes it at once and says why the
    /// open was refused, if it was. Never throws, and changes nothing in the file.
    /// </summary>
    DatFileWriteCheck CheckFile(string filePath);
}
