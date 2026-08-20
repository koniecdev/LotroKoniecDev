using System.Runtime.InteropServices;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Infrastructure.DatFile;

/// <summary>
/// Managed access to LOTRO DAT files, on top of the native datexport.dll.
/// </summary>
public sealed class DatFileHandler : IDatFileHandler, IDatVersionReader
{
    private readonly Lock _lock = new();
    private readonly HashSet<int> _openHandles = [];
    private bool _disposed;

    /// <param name="access">
    /// How to open the file. <see cref="DatFileAccess.Read"/> opens it without asking for write
    /// permission, which was checked against the live Program Files DAT without elevation (#629).
    /// </param>
    /// <returns>The handle of the open file, or a failure explaining why it could not be opened.</returns>
    public Result<int> Open(string datFilePath, DatFileAccess access)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        if (!File.Exists(datFilePath))
        {
            return Result.Failure<int>(DomainErrors.DatFile.NotFound(datFilePath));
        }

        const int requestedHandle = 0;
        byte[] datIdStamp = new byte[64];
        byte[] firstIterGuid = new byte[64];

        uint openFlags = access switch
        {
            DatFileAccess.ReadWrite => DatExportNative.OpenFlagsReadWrite,
            _ => DatExportNative.OpenFlagsRead
        };

        try
        {
            int result = DatExportNative.OpenDatFileEx2(
                requestedHandle,
                datFilePath,
                openFlags,
                out _,
                out _,
                out _,
                out _,
                out _,
                datIdStamp,
                firstIterGuid);

            if (result != requestedHandle)
            {
                return Result.Failure<int>(DomainErrors.DatFile.CannotOpen(datFilePath));
            }

            lock (_lock)
            {
                _openHandles.Add(result);
            }

            return Result.Success(result);
        }
        catch (DllNotFoundException)
        {
            return Result.Failure<int>(DomainErrors.DatFile.CannotOpen(
                $"{datFilePath}: datexport.dll not found. Ensure the DLL is in the application directory."));
        }
        catch (Exception ex)
        {
            return Result.Failure<int>(DomainErrors.DatFile.CannotOpen($"{datFilePath}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Reads the DAT and game-data version numbers. It opens the file read-only and closes it at once.
    /// </summary>
    public Result<DatVersionInfo> ReadVersion(string datFilePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        if (!File.Exists(datFilePath))
        {
            return Result.Failure<DatVersionInfo>(DomainErrors.DatFile.NotFound(datFilePath));
        }

        const int requestedHandle = 1;
        byte[] datIdStamp = new byte[64];
        byte[] firstIterGuid = new byte[64];

        try
        {
            int result = DatExportNative.OpenDatFileEx2(
                requestedHandle,
                datFilePath,
                DatExportNative.OpenFlagsRead,
                out _,
                out _,
                out int vnumDatFile,
                out int vnumGameData,
                out _,
                datIdStamp,
                firstIterGuid);

            if (result != requestedHandle)
            {
                return Result.Failure<DatVersionInfo>(DomainErrors.DatFile.CannotOpen(datFilePath));
            }

            DatExportNative.CloseDatFile(result);

            return Result.Success(new DatVersionInfo(vnumDatFile, vnumGameData));
        }
        catch (DllNotFoundException)
        {
            return Result.Failure<DatVersionInfo>(DomainErrors.DatFile.CannotOpen(
                $"{datFilePath}: datexport.dll not found. Ensure the DLL is in the application directory."));
        }
        catch (Exception ex)
        {
            return Result.Failure<DatVersionInfo>(DomainErrors.DatFile.CannotOpen($"{datFilePath}: {ex.Message}"));
        }
    }

    public Dictionary<int, (int Size, int Iteration)> GetAllSubfileSizes(int handle)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        int count = DatExportNative.GetNumSubfiles(handle);

        if (count <= 0)
        {
            return [];
        }

        int[] fileIds = new int[count];
        int[] sizes = new int[count];
        int[] iterations = new int[count];

        DatExportNative.GetSubfileSizes(handle, fileIds, sizes, iterations, 0, count);

        Dictionary<int, (int, int)> result = new(count);

        for (int i = 0; i < count; i++)
        {
            result[fileIds[i]] = (sizes[i], iterations[i]);
        }

        return result;
    }

    public int GetSubfileVersion(int handle, int fileId)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        return DatExportNative.GetSubfileVersion(handle, fileId);
    }

    public Result<byte[]> GetSubfileData(int handle, int fileId, int size)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        if (size <= 0)
        {
            return Result.Failure<byte[]>(
                DomainErrors.DatFile.ReadError(fileId, "Invalid size (must be positive)"));
        }

        IntPtr buffer = IntPtr.Zero;

        try
        {
            buffer = Marshal.AllocHGlobal(size);
            DatExportNative.GetSubfileData(handle, fileId, buffer, 0, out _);

            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);

            return Result.Success(data);
        }
        catch (OutOfMemoryException)
        {
            return Result.Failure<byte[]>(
                DomainErrors.DatFile.ReadError(fileId, $"Out of memory allocating {size} bytes"));
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>(
                DomainErrors.DatFile.ReadError(fileId, ex.Message));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <param name="fileId">The subfile to write. It must already exist in the DAT.</param>
    /// <param name="data">The new content. It must not be null or empty.</param>
    /// <param name="version">The version to give the subfile after the write.</param>
    /// <param name="iteration">The iteration number to give the subfile after the write.</param>
    public Result PutSubfileData(
        int handle,
        int fileId,
        byte[] data,
        int version,
        int iteration)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            return Result.Failure(
                DomainErrors.DatFile.WriteError(fileId, "Cannot write empty data"));
        }

        IntPtr buffer = IntPtr.Zero;

        try
        {
            buffer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);
            int purgeResult = DatExportNative.PurgeSubfileData(handle, fileId);
            if (purgeResult < 0)
            {
                return Result.Failure(
                    DomainErrors.DatFile.WriteError(fileId, $"PurgeSubfileData failed with code {purgeResult}"));
            }
            int result = DatExportNative.PutSubfileData(
                handle,
                fileId,
                buffer,
                0,
                data.Length,
                version,
                iteration,
                0);
            if (result == 0)
            {
                return Result.Failure(
                    DomainErrors.DatFile.WriteError(fileId, $"PutSubfileData failed with code {result}"));
            }
            return Result.Success();
        }
        catch (OutOfMemoryException)
        {
            return Result.Failure(
                DomainErrors.DatFile.WriteError(fileId, $"Out of memory allocating {data.Length} bytes"));
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.DatFile.WriteError(fileId, ex.Message));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Flush(int handle)
    {
        ThrowIfDisposed();
        ValidateHandle(handle);

        DatExportNative.Flush(handle);
    }

    public void Close(int handle)
    {
        if (_disposed)
        {
            return;
        }

        bool shouldClose;
        lock (_lock)
        {
            shouldClose = _openHandles.Remove(handle);
        }

        if (!shouldClose)
        {
            return;
        }

        try
        {
            DatExportNative.CloseDatFile(handle);
        }
        catch
        {
            // A failure to close tells us nothing useful here, so it is ignored.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        int[] handlesToClose;
        lock (_lock)
        {
            handlesToClose = [.. _openHandles];
            _openHandles.Clear();
            _disposed = true;
        }

        foreach (int handle in handlesToClose)
        {
            try
            {
                DatExportNative.CloseDatFile(handle);
            }
            catch
            {
                // A failure while disposing tells us nothing useful here, so it is ignored.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ValidateHandle(int handle)
    {
        bool isValid;
        lock (_lock)
        {
            isValid = _openHandles.Contains(handle);
        }

        if (!isValid)
        {
            throw new ArgumentException($"Invalid or closed file handle: {handle}", nameof(handle));
        }
    }
}
