using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Provides an abstraction for DAT file operations.
/// </summary>
public interface IDatFileHandler : IDisposable
{
    /// <summary>
    /// Opens a DAT file and returns the file handle.
    /// </summary>
    /// <param name="path">Path to the DAT file.</param>
    /// <returns>Result containing the file handle or an error.</returns>
    Result<int> Open(string path);

    /// <summary>
    /// Gets sizes and iterations of all subfiles in the DAT archive.
    /// </summary>
    /// <param name="handle">The DAT file handle.</param>
    /// <returns>Dictionary mapping file IDs to their sizes and iterations.</returns>
    Dictionary<int, (int Size, int Iteration)> GetAllSubfileSizes(int handle);

    /// <summary>
    /// Gets the version of a specific subfile.
    /// </summary>
    int GetSubfileVersion(int handle, int fileId);

    /// <summary>
    /// Reads raw data from a subfile.
    /// </summary>
    /// <param name="handle">The DAT file handle.</param>
    /// <param name="fileId">The subfile ID.</param>
    /// <param name="size">The size of data to read.</param>
    /// <returns>Result containing the raw bytes or an error.</returns>
    Result<byte[]> GetSubfileData(int handle, int fileId, int size);

    /// <summary>
    /// Writes raw data to a subfile.
    /// </summary>
    /// <param name="handle">The DAT file handle.</param>
    /// <param name="fileId">The subfile ID.</param>
    /// <param name="data">The data to write.</param>
    /// <param name="version">The version number.</param>
    /// <param name="iteration">The iteration number.</param>
    /// <returns>Result indicating success or an error.</returns>
    Result PutSubfileData(int handle, int fileId, byte[] data, int version, int iteration);

    /// <summary>
    /// Flushes pending changes to disk.
    /// </summary>
    void Flush(int handle);

    /// <summary>
    /// Closes the DAT file.
    /// </summary>
    void Close(int handle);
}
