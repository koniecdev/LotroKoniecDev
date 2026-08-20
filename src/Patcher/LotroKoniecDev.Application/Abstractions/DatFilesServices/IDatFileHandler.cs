using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Abstractions.DatFilesServices;

/// <summary>
/// The port the application layer uses to read and write a LOTRO DAT file.
/// </summary>
public interface IDatFileHandler : IDisposable
{
    /// <param name="access">Read-only or read-write.</param>
    Result<int> Open(string datFilePath, DatFileAccess access);

    /// <returns>Every subfile id in the archive, with its size and iteration number.</returns>
    Dictionary<int, (int Size, int Iteration)> GetAllSubfileSizes(int handle);

    int GetSubfileVersion(int handle, int fileId);

    /// <param name="size">How many bytes to read. Take it from <see cref="GetAllSubfileSizes"/>.</param>
    Result<byte[]> GetSubfileData(int handle, int fileId, int size);

    /// <param name="version">The version to give the subfile after the write.</param>
    /// <param name="iteration">The iteration number to give the subfile after the write.</param>
    Result PutSubfileData(int handle, int fileId, byte[] data, int version, int iteration);

    /// <summary>Writes everything still buffered for this file to disk.</summary>
    void Flush(int handle);

    void Close(int handle);
}
