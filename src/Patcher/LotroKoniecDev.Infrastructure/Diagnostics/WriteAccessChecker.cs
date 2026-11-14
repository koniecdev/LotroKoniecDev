using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Infrastructure.Diagnostics;

public sealed class WriteAccessChecker : IWriteAccessChecker
{
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    public bool CanWriteTo(string directoryPath)
    {
        try
        {
            string testFile = Path.Combine(directoryPath,
                $".lotrokoniec_writetest_{Guid.NewGuid():N}");

            using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public DatFileWriteCheck CheckFile(string filePath)
    {
        try
        {
            // .NET reports the Read-only flag and a refusing ACL with the same exception, so the
            // flag is read first to tell the two apart.
            if (File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly))
            {
                return new DatFileWriteCheck(DatFileWriteRefusal.ReadOnly);
            }

            // The native open asks for the same access. Its share mode is not known, so this open
            // shares as much as it can: a stricter one could refuse a file the patch can open.
            using (File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
            }

            return new DatFileWriteCheck(DatFileWriteRefusal.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DatFileWriteCheck(DatFileWriteRefusal.NotFound, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DatFileWriteCheck(DatFileWriteRefusal.AccessDenied, ex.Message);
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult)
        {
            return new DatFileWriteCheck(DatFileWriteRefusal.InUse, ex.Message);
        }
        catch (Exception ex)
        {
            // The preflight has no catch of its own, and an unknown refusal still deserves its reason.
            return new DatFileWriteCheck(DatFileWriteRefusal.Other, ex.Message);
        }
    }
}
