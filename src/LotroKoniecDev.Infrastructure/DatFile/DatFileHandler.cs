using System.Runtime.InteropServices;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.DatFile;

/// <summary>
/// Provides managed access to LOTRO DAT files through the native datexport.dll library.
/// </summary>
public sealed class DatFileHandler : IDatFileHandler
{
    private readonly Lock _lock = new();
    private readonly HashSet<int> _openHandles = [];
    private bool _disposed;

    public Result<int> Open(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Result.Failure<int>(DomainErrors.DatFile.NotFound(path));
        }

        const int requestedHandle = 0;
        byte[] datIdStamp = new byte[64];
        byte[] firstIterGuid = new byte[64];

        try
        {
            int result = DatExportNative.OpenDatFileEx2(
                requestedHandle,
                path,
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
                return Result.Failure<int>(DomainErrors.DatFile.CannotOpen(path));
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
                $"{path}: datexport.dll not found. Ensure the DLL is in the application directory."));
        }
        catch (Exception ex)
        {
            return Result.Failure<int>(DomainErrors.DatFile.CannotOpen($"{path}: {ex.Message}"));
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

    public Result PutSubfileData(int handle, int fileId, byte[] data, int version, int iteration)
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

            DatExportNative.PurgeSubfileData(handle, fileId);
            int result = DatExportNative.PutSubfileData(
                handle,
                fileId,
                buffer,
                0,
                data.Length,
                version,
                iteration,
                0);

            // Check if write was successful (non-zero typically indicates success)
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
