using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// The patch steps before a launch (spec 0001, spec 0012 Tier 0), shared by the launch and the desktop
/// app's Patch button (#42). The launch passes no progress, because the CLI's progress port prints a
/// line per report; the button passes the main window's progress bar.
/// </summary>
internal sealed partial class PatchIfNeededService : IPatchIfNeededService
{
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _gameVersionFileStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IPatchingService _patchingService;
    private readonly ISubFileWipeDetector _wipeDetector;
    private readonly ILogger<PatchIfNeededService> _logger;

    public Result<PatchIfNeededResponse> PatchIfNeeded(
        string datFilePath,
        string gameVersionFilePath,
        string translationFilePath,
        IProgress<OperationProgress>? progress = null)
    {
        if (_gameProcessDetector.IsLotroRunning())
        {
            LogBlockedGameAlreadyRunning(_logger);
            return Result.Failure<PatchIfNeededResponse>(DomainErrors.GameLaunch.GameAlreadyRunning);
        }

        LogComputingTranslationHash(_logger);
        Result<string> hashResult = _fileHasher.ComputeHash(translationFilePath);
        if (hashResult.IsFailure)
        {
            LogComputeHashFailed(_logger, hashResult.Error.Message);
            return Result.Failure<PatchIfNeededResponse>(hashResult.Error);
        }
        string currentHash = hashResult.Value;

        LogCurrentTranslationHash(_logger, currentHash);

        LogReadingStoredVersion(_logger);

        Result<StoredVersionInfo?> storedResult = _gameVersionFileStore.ReadStoredVersion(gameVersionFilePath);
        if (storedResult.IsFailure)
        {
            LogReadStoredVersionFailed(_logger, storedResult.Error.Message);
            return Result.Failure<PatchIfNeededResponse>(storedResult.Error);
        }
        StoredVersionInfo? storedInfo = storedResult.Value;

        LogStoredVersionInfo(
            _logger,
            storedInfo?.ForumVersion ?? "(null)",
            storedInfo?.VnumDatFile?.ToString() ?? "(null)",
            storedInfo?.VnumGameData?.ToString() ?? "(null)",
            storedInfo?.TranslationFileHash ?? "(null)");

        bool translationChanged = storedResult.Value?.TranslationFileHash is null
            || !string.Equals(currentHash, storedResult.Value.TranslationFileHash, StringComparison.Ordinal);

        LogTranslationChangeEvaluated(
            _logger,
            translationChanged,
            storedInfo?.TranslationFileHash is null,
            storedInfo?.TranslationFileHash is not null
            && string.Equals(currentHash, storedInfo.TranslationFileHash, StringComparison.Ordinal));

        bool translationsApplied = false;
        int appliedCount = 0;
        int skippedCount = 0;
        int wipedSubFileCount = 0;
        string? repairFailure = null;

        if (translationChanged)
        {
            LogPatchingTranslationChanged(_logger);

            Result<PatchSummaryResponse> patchResult =
                _patchingService.ApplyTranslations(translationFilePath, datFilePath, progress);
            if (patchResult.IsFailure)
            {
                LogApplyTranslationsFailed(_logger, patchResult.Error.Message);
                return Result.Failure<PatchIfNeededResponse>(
                    DomainErrors.GameLaunch.RepatchFailed(patchResult.Error.Message));
            }

            PatchSummaryResponse patchSummary = patchResult.Value;

            // A rejected line must reach a person (ADR-0042). The CLI's `patch` prints the summary; the
            // launch and the desktop app have only the log.
            foreach (string warning in patchSummary.Warnings)
            {
                LogPatchWarning(_logger, warning);
            }

            translationsApplied = true;
            appliedCount = patchSummary.AppliedTranslations;
            skippedCount = patchSummary.SkippedTranslations;

            LogTranslationsPatched(_logger, appliedCount, skippedCount);

            LogReadingDatVnum(_logger);

            Result<DatVersionInfo> vnumResult = _datVersionReader.ReadVersion(datFilePath);
            if (vnumResult.IsFailure)
            {
                LogReadVersionFailed(_logger, vnumResult.Error.Message);
                return Result.Failure<PatchIfNeededResponse>(vnumResult.Error);
            }
            DatVersionInfo datVersion = vnumResult.Value;

            LogDatVnumRead(_logger, datVersion.VnumDatFile, datVersion.VnumGameData);

            Result saveResult = _gameVersionFileStore.SaveVersion(
                gameVersionFilePath,
                storedResult.Value?.ForumVersion,
                datVersion.VnumDatFile,
                datVersion.VnumGameData,
                currentHash);
            if (saveResult.IsFailure)
            {
                LogSaveVersionFailed(_logger, saveResult.Error.Message);
                return Result.Failure<PatchIfNeededResponse>(saveResult.Error);
            }
            LogVersionSaved(_logger);
        }
        else
        {
            LogSkippedTranslationUnchanged(_logger);

            // Spec 0012 Tier 0: the file is the one we applied, but a game update may since have
            // replaced subfiles we patched, and the hash cannot see that. When the translation file
            // changed, the patch above already covers it.
            WipedSubFileSet wiped = _wipeDetector.Detect(datFilePath, translationFilePath);
            wipedSubFileCount = wiped.Count;

            if (!wiped.IsEmpty)
            {
                LogRepairingWipedSubFiles(_logger, wiped.Count);

                // No staleness verdict gates this, and it works offline: the source guard admits each
                // row on its own (ADR-0047 §5). A successful patch saves a new snapshot, so the next
                // run finds nothing to repair.
                Result<PatchSummaryResponse> repairResult =
                    _patchingService.ApplyTranslations(translationFilePath, datFilePath, progress);

                if (repairResult.IsFailure)
                {
                    // A failed repair never stops the run (spec 0012 Tier 0, "Built" note): the launch
                    // still starts the game. The old snapshot stays, so the next run tries again.
                    LogRepairFailed(_logger, repairResult.Error.Message);
                    repairFailure = repairResult.Error.Message;
                }
                else
                {
                    PatchSummaryResponse repairSummary = repairResult.Value;

                    foreach (string warning in repairSummary.Warnings)
                    {
                        LogPatchWarning(_logger, warning);
                    }

                    translationsApplied = true;
                    appliedCount = repairSummary.AppliedTranslations;
                    skippedCount = repairSummary.SkippedTranslations;

                    LogWipedSubFilesRepaired(_logger, appliedCount, skippedCount);
                }
            }
        }

        return Result.Success(new PatchIfNeededResponse(
            translationChanged,
            translationsApplied,
            appliedCount,
            skippedCount,
            wipedSubFileCount,
            repairFailure));
    }

    public PatchIfNeededService(
        IGameProcessDetector gameProcessDetector,
        IFileHasher fileHasher,
        IGameVersionFileStore gameVersionFileStore,
        IDatVersionReader datVersionReader,
        IPatchingService patchingService,
        ISubFileWipeDetector wipeDetector,
        ILogger<PatchIfNeededService> logger)
    {
        _gameProcessDetector = gameProcessDetector;
        _fileHasher = fileHasher;
        _gameVersionFileStore = gameVersionFileStore;
        _datVersionReader = datVersionReader;
        _patchingService = patchingService;
        _wipeDetector = wipeDetector;
        _logger = logger;
    }

    [LoggerMessage(EventId = EventIds.LaunchBlockedGameAlreadyRunning, Level = LogLevel.Warning, Message = "BLOCKED: LOTRO already running")]
    private static partial void LogBlockedGameAlreadyRunning(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchComputingTranslationHash, Level = LogLevel.Information, Message = "Step 1: Computing translation file hash...")]
    private static partial void LogComputingTranslationHash(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchComputeHashFailed, Level = LogLevel.Error, Message = "ComputeHash FAILED: {Error}")]
    private static partial void LogComputeHashFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchCurrentTranslationHash, Level = LogLevel.Information, Message = "Current translation hash: {Hash}")]
    private static partial void LogCurrentTranslationHash(ILogger logger, string hash);

    [LoggerMessage(EventId = EventIds.LaunchReadingStoredVersion, Level = LogLevel.Information, Message = "Step 2: Reading stored version info...")]
    private static partial void LogReadingStoredVersion(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchReadStoredVersionFailed, Level = LogLevel.Error, Message = "ReadStoredVersion FAILED: {Error}")]
    private static partial void LogReadStoredVersionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchStoredVersionInfo, Level = LogLevel.Information, Message = "Stored info: ForumVersion={Forum}, VnumDat={VnumDat}, VnumGame={VnumGame}, Hash={Hash}")]
    private static partial void LogStoredVersionInfo(ILogger logger, string forum, string vnumDat, string vnumGame, string hash);

    [LoggerMessage(EventId = EventIds.LaunchTranslationChangeEvaluated, Level = LogLevel.Information, Message = "Translation changed? {Changed} (stored hash null={IsNull}, match={Match})")]
    private static partial void LogTranslationChangeEvaluated(ILogger logger, bool changed, bool isNull, bool match);

    [LoggerMessage(EventId = EventIds.LaunchPatchingTranslationChanged, Level = LogLevel.Information, Message = ">>> PATCHING: Translation file changed — applying patch")]
    private static partial void LogPatchingTranslationChanged(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchApplyTranslationsFailed, Level = LogLevel.Error, Message = "ApplyTranslations FAILED: {Error}")]
    private static partial void LogApplyTranslationsFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchTranslationsPatched, Level = LogLevel.Information, Message = "Patched translations: {Applied} applied, {Skipped} skipped")]
    private static partial void LogTranslationsPatched(ILogger logger, int applied, int skipped);

    [LoggerMessage(EventId = EventIds.LaunchPatchWarning, Level = LogLevel.Warning, Message = "Patch warning: {Warning}")]
    private static partial void LogPatchWarning(ILogger logger, string warning);

    [LoggerMessage(EventId = EventIds.LaunchReadingDatVnum, Level = LogLevel.Information, Message = "Reading DAT vnum to save alongside hash...")]
    private static partial void LogReadingDatVnum(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchReadVersionFailed, Level = LogLevel.Error, Message = "ReadVersion FAILED: {Error}")]
    private static partial void LogReadVersionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchDatVnumRead, Level = LogLevel.Information, Message = "DAT vnum: VnumDat={VnumDat}, VnumGame={VnumGame}")]
    private static partial void LogDatVnumRead(ILogger logger, int vnumDat, int vnumGame);

    [LoggerMessage(EventId = EventIds.LaunchSaveVersionFailed, Level = LogLevel.Error, Message = "SaveVersion FAILED: {Error}")]
    private static partial void LogSaveVersionFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchVersionSaved, Level = LogLevel.Information, Message = "Version saved OK")]
    private static partial void LogVersionSaved(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchSkippedTranslationUnchanged, Level = LogLevel.Information, Message = ">>> SKIP: Translation file unchanged — skipping patch")]
    private static partial void LogSkippedTranslationUnchanged(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchRepairingWipedSubFiles, Level = LogLevel.Information, Message = ">>> REPAIRING: a game update replaced {Count} text subfile(s) since our last patch — re-patching from the local translation file")]
    private static partial void LogRepairingWipedSubFiles(ILogger logger, int count);

    [LoggerMessage(EventId = EventIds.LaunchRepairFailed, Level = LogLevel.Warning, Message = "Repair FAILED: {Error}. The old snapshot stays, so the next patch or launch tries again")]
    private static partial void LogRepairFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchWipedSubFilesRepaired, Level = LogLevel.Information, Message = "Repaired translations: {Applied} applied, {Skipped} skipped")]
    private static partial void LogWipedSubFilesRepaired(ILogger logger, int applied, int skipped);
}
