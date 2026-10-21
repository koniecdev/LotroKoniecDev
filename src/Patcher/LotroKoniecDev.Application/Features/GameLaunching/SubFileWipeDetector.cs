using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

/// <summary>
/// Compares the iteration snapshot saved after our last patch with the DAT as it is now (spec 0012
/// Tier 0). The DAT is opened read-only, which needs no elevation (#629), and closed again before
/// the launch writes anything.
/// </summary>
internal sealed partial class SubFileWipeDetector : ISubFileWipeDetector
{
    private const int LoggedFileIdSampleSize = 10;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ISubFileIterationSnapshotStore _snapshotStore;
    private readonly ILogger<SubFileWipeDetector> _logger;

    public WipedSubFileSet Detect(string datFilePath, string translationFilePath)
    {
        Result<SubFileIterationSnapshot?> storedResult = _snapshotStore.Read(translationFilePath);
        if (storedResult.IsFailure)
        {
            LogSnapshotUnreadable(_logger, storedResult.Error.Message);
            return WipedSubFileSet.None;
        }

        if (storedResult.Value is not { } stored)
        {
            LogNoSnapshot(_logger);
            return WipedSubFileSet.None;
        }

        Result<int> openResult = _datFileHandler.Open(datFilePath, DatFileAccess.Read);
        if (openResult.IsFailure)
        {
            LogDatUnreadable(_logger, openResult.Error.Message);
            return WipedSubFileSet.None;
        }

        SubFileIterationSnapshot current;

        try
        {
            current = SubFileIterationSnapshot.FromSubfileSizes(
                _datFileHandler.GetAllSubfileSizes(openResult.Value));
        }
        finally
        {
            _datFileHandler.Close(openResult.Value);
        }

        // A real update never removes every text subfile, so an empty list is a failed read. Taking
        // it at face value would report every subfile as removed and force a repair.
        if (current.Count == 0)
        {
            LogDatListsNoTextSubFiles(_logger);
            return WipedSubFileSet.None;
        }

        WipedSubFileSet wiped = stored.FindWipedIn(current);

        if (wiped.IsEmpty)
        {
            LogNoWipe(_logger, stored.Count);
        }
        else
        {
            LogWipeDetected(
                _logger,
                wiped.Count,
                wiped.IterationMoved.Count,
                wiped.Removed.Count,
                string.Join(", ", wiped.IterationMoved.Concat(wiped.Removed).Take(LoggedFileIdSampleSize)));
        }

        return wiped;
    }

    public SubFileWipeDetector(
        IDatFileHandler datFileHandler,
        ISubFileIterationSnapshotStore snapshotStore,
        ILogger<SubFileWipeDetector> logger)
    {
        _datFileHandler = datFileHandler;
        _snapshotStore = snapshotStore;
        _logger = logger;
    }

    [LoggerMessage(EventId = EventIds.LaunchWipeSnapshotUnreadable, Level = LogLevel.Warning, Message = "Tier 0: the iteration snapshot cannot be read ({Error}). No update detection until the next patch saves a new one")]
    private static partial void LogSnapshotUnreadable(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchWipeNoSnapshot, Level = LogLevel.Information, Message = "Tier 0: no iteration snapshot yet. Update detection starts after the next patch")]
    private static partial void LogNoSnapshot(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchWipeDatUnreadable, Level = LogLevel.Warning, Message = "Tier 0: the DAT cannot be opened read-only to compare iterations ({Error}). No update detection on this launch")]
    private static partial void LogDatUnreadable(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchWipeDatListsNoTextSubFiles, Level = LogLevel.Warning, Message = "Tier 0: the DAT lists no text subfile, so the read failed. No update detection on this launch")]
    private static partial void LogDatListsNoTextSubFiles(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchWipeNotDetected, Level = LogLevel.Information, Message = "Tier 0: all {Count} text subfiles are as our last patch left them")]
    private static partial void LogNoWipe(ILogger logger, int count);

    [LoggerMessage(EventId = EventIds.LaunchWipeDetected, Level = LogLevel.Information, Message = "Tier 0: a game update replaced {Count} text subfile(s) since our last patch ({IterationMoved} with a new iteration, {Removed} removed). First file ids: {FileIds}")]
    private static partial void LogWipeDetected(ILogger logger, int count, int iterationMoved, int removed, string fileIds);
}
