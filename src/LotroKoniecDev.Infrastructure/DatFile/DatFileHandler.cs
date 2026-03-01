using System.Runtime.InteropServices;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Infrastructure.DatFile;

/// <summary>
/// Provides managed access to LOTRO DAT files through the native datexport.dll library.
/// </summary>
public sealed class DatFileHandler : IDatFileHandler, IDatVersionReader
{
    private readonly IDatFileProtector _protector;
    private readonly Lock _lock = new();
    private readonly HashSet<int> _openHandles = [];
    private bool _disposed;

    public DatFileHandler(IDatFileProtector protector)
    {
        _protector = protector;
    }

    /// <summary>
    /// Opens a LOTRO DAT file given its file path and returns a handle to the open file.
    /// </summary>
    /// <param name="datFilePath">The file path to the DAT file to be opened. It must not be null, empty, or whitespace.</param>
    /// <returns>
    /// A <see cref="Result{TValue}"/> containing the handle to the opened DAT file if the operation is successful;
    /// otherwise, a failure result with an error message describing why the file could not be opened.
    /// </returns>
    public Result<int> Open(string datFilePath)
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

        try
        {
            int result = DatExportNative.OpenDatFileEx2(
                requestedHandle,
                datFilePath,
                DatExportNative.OpenFlagsReadWrite,
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

        Result<bool> wasProtected = _protector.IsProtected(datFilePath);
        if (wasProtected.IsFailure)
        {
            return Result.Failure<DatVersionInfo>(wasProtected.Error);
        }

        if (wasProtected.Value)
        {
            Result unprotectResult = _protector.Unprotect(datFilePath);
            if (unprotectResult.IsFailure)
            {
                return Result.Failure<DatVersionInfo>(unprotectResult.Error);
            }
        }

        try
        {
            int result = DatExportNative.OpenDatFileEx2(
                requestedHandle,
                datFilePath,
                DatExportNative.OpenFlagsReadWrite,
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
        finally
        {
            if (wasProtected.Value)
            {
                _protector.Protect(datFilePath);
            }
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

    /// <summary>
    /// Writes a subfile to a LOTRO DAT file, specifying its data, version, and iteration information.
    /// </summary>
    /// <param name="handle">The handle to the open DAT file where the subfile data will be written. Must be a valid handle.</param>
    /// <param name="fileId">The identifier of the subfile to write. Must correspond to a valid subfile entry in the DAT file.</param>
    /// <param name="data">The byte array containing the data to be written to the subfile. Must not be null or empty.</param>
    /// <param name="version">The version of the subfile being written. Typically used for managing multiple subfile revisions.</param>
    /// <param name="iteration">The iteration value of the subfile, used to track updates or changes.</param>
    /// <returns>
    /// A <see cref="Result"/> indicating success if the subfile was written successfully;
    /// otherwise, a failure result containing an error message with details about the failure.
    /// </returns>
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
            // Ignore errors during close
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
                // Ignore errors during dispose
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
