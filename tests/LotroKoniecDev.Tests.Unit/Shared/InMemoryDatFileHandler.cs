using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Shared;

/// <summary>
/// A DAT kept in memory, for tests that follow one DAT through a patch, a game update and the next
/// launches. A write keeps the version and iteration it is given, as datexport does. The test plays
/// SSG's part: it replaces a subfile with a new iteration, or removes it.
/// </summary>
internal sealed class InMemoryDatFileHandler : IDatFileHandler
{
    private const int Handle = 1;
    private const int SubFileVersion = 1;

    private readonly Dictionary<int, (byte[] Data, int Version, int Iteration)> _subFiles = [];

    public int WriteCount { get; private set; }

    /// <summary>Makes every read-write open fail, as it does for a launch without elevation.</summary>
    public bool RefuseWriteAccess { get; set; }

    /// <summary>Adds a text subfile with one fragment, id 1, that holds <paramref name="text"/>.</summary>
    public void AddTextSubFile(int fileId, string text, int iteration)
        => _subFiles[fileId] = (TestDataFactory.CreateTextSubFileData(fileId, text), SubFileVersion, iteration);

    /// <summary>Adds a subfile of another kind, such as an image. The patcher never writes those.</summary>
    public void AddNonTextSubFile(int fileId, int iteration)
        => _subFiles[fileId] = (BitConverter.GetBytes(fileId), SubFileVersion, iteration);

    /// <summary>What a game update does to a subfile it replaces: new content and a new iteration.</summary>
    public void ReplaceTextSubFile(int fileId, string text, int iteration) => AddTextSubFile(fileId, text, iteration);

    public void RemoveSubFile(int fileId) => _subFiles.Remove(fileId);

    public string TextOf(int fileId, ulong fragmentId)
    {
        SubFile subFile = new();
        subFile.Parse(_subFiles[fileId].Data);

        return string.Join(DatFileConstants.PieceSeparator, subFile.Fragments[fragmentId].Pieces);
    }

    public Result<int> Open(string datFilePath, DatFileAccess access)
        => access == DatFileAccess.ReadWrite && RefuseWriteAccess
            ? Result.Failure<int>(DomainErrors.DatFile.CannotOpen(datFilePath))
            : Result.Success(Handle);

    public Dictionary<int, (int Size, int Iteration)> GetAllSubfileSizes(int handle)
        => _subFiles.ToDictionary(entry => entry.Key, entry => (entry.Value.Data.Length, entry.Value.Iteration));

    public int GetSubfileVersion(int handle, int fileId) => _subFiles[fileId].Version;

    public Result<byte[]> GetSubfileData(int handle, int fileId, int size)
        => Result.Success(_subFiles[fileId].Data.ToArray());

    public Result PutSubfileData(int handle, int fileId, byte[] data, int version, int iteration)
    {
        _subFiles[fileId] = (data.ToArray(), version, iteration);
        WriteCount++;

        return Result.Success();
    }

    public void Flush(int handle)
    {
    }

    public void Close(int handle)
    {
    }

    public void Dispose()
    {
    }
}
