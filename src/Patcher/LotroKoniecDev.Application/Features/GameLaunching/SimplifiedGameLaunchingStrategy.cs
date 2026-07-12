using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed partial class SimplifiedGameLaunchingStrategy : IGameLaunchingStrategy
{
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _gameVersionFileStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IPatchingService _patchingService;
    private readonly IGameLauncher _gameLauncher;
    private readonly ILogger<SimplifiedGameLaunchingStrategy> _logger;

    public SimplifiedGameLaunchingStrategy(
        IGameProcessDetector gameProcessDetector,
        IFileHasher fileHasher,
        IGameVersionFileStore gameVersionFileStore,
        IDatVersionReader datVersionReader,
        IPatchingService patchingService,
        IGameLauncher gameLauncher,
        ILogger<SimplifiedGameLaunchingStrategy> logger)
    {
        _gameProcessDetector = gameProcessDetector;
        _fileHasher = fileHasher;
        _gameVersionFileStore = gameVersionFileStore;
        _datVersionReader = datVersionReader;
        _patchingService = patchingService;
        _gameLauncher = gameLauncher;
        _logger = logger;
    }

    public ValueTask<Result<GameLaunchingResponse>> ExecuteAsync(
        GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        LogLaunchStarted(_logger);
        LogDatFilePath(_logger, command.DatFilePath);
        LogTranslationFilePath(_logger, command.TranslationFilePath);
        LogGameVersionFilePath(_logger, command.GameVersionFilePath);

        // 1. Is the game already running?
        if (_gameProcessDetector.IsLotroRunning())
        {
            LogBlockedGameAlreadyRunning(_logger);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(DomainErrors.GameLaunch.GameAlreadyRunning));
        }

        // 2. Has the translation file changed since last apply?
        LogComputingTranslationHash(_logger);
        Result<string> hashResult = _fileHasher.ComputeHash(command.TranslationFilePath);
        if (hashResult.IsFailure)
        {
            LogComputeHashFailed(_logger, hashResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(hashResult.Error));
        }
        string currentHash = hashResult.Value;

        LogCurrentTranslationHash(_logger, currentHash);

        LogReadingStoredVersion(_logger);

        Result<StoredVersionInfo?> storedResult = _gameVersionFileStore.ReadStoredVersion(command.GameVersionFilePath);
        if (storedResult.IsFailure)
        {
            LogReadStoredVersionFailed(_logger, storedResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(storedResult.Error));
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

        if (translationChanged)
        {
            LogPatchingTranslationChanged(_logger);

            Result<PatchSummaryResponse> patchResult =
                _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
            if (patchResult.IsFailure)
            {
                LogApplyTranslationsFailed(_logger, patchResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(
                        DomainErrors.GameLaunch.RepatchFailed(patchResult.Error.Message)));
            }

            PatchSummaryResponse patchSummary = patchResult.Value;

            translationsApplied = true;
            appliedCount = patchSummary.AppliedTranslations;
            skippedCount = patchSummary.SkippedTranslations;

            LogTranslationsPatched(_logger, appliedCount, skippedCount);

            // Save new hash + current vnum
            LogReadingDatVnum(_logger);

            Result<DatVersionInfo> vnumResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (vnumResult.IsFailure)
            {
                LogReadVersionFailed(_logger, vnumResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(vnumResult.Error));
            }
            DatVersionInfo datVersion = vnumResult.Value;

            LogDatVnumRead(_logger, datVersion.VnumDatFile, datVersion.VnumGameData);

            Result saveResult = _gameVersionFileStore.SaveVersion(
                command.GameVersionFilePath,
                storedResult.Value?.ForumVersion,
                datVersion.VnumDatFile,
                datVersion.VnumGameData,
                currentHash);
            if (saveResult.IsFailure)
            {
                LogSaveVersionFailed(_logger, saveResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(saveResult.Error));
            }
            LogVersionSaved(_logger);
        }
        else
        {
            LogSkippedTranslationUnchanged(_logger);
        }

        // 3. Launch (fire-and-forget)
        LogStartingGame(_logger);
        Result launchResult = _gameLauncher.Launch(command.DatFilePath);
        if (launchResult.IsFailure)
        {
            LogGameLaunchFailed(_logger, launchResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(launchResult.Error));
        }

        LogLauncherStarted(_logger);
        LogLaunchEnded(_logger);

        GameLaunchingResponse response = new(
            ForumVersion: null,
            UpdateWasDetected: false,
            GameExitCode: 0,
            TranslationsApplied: translationsApplied,
            AppliedCount: appliedCount,
            SkippedCount: skippedCount);

        return ValueTask.FromResult(Result.Success(response));
    }

    [LoggerMessage(EventId = EventIds.LaunchStarted, Level = LogLevel.Information, Message = "=== SIMPLIFIED LAUNCH START ===")]
    private static partial void LogLaunchStarted(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchDatFilePath, Level = LogLevel.Information, Message = "DatFilePath: {DatFilePath}")]
    private static partial void LogDatFilePath(ILogger logger, string datFilePath);

    [LoggerMessage(EventId = EventIds.LaunchTranslationFilePath, Level = LogLevel.Information, Message = "TranslationFilePath: {TranslationFilePath}")]
    private static partial void LogTranslationFilePath(ILogger logger, string translationFilePath);

    [LoggerMessage(EventId = EventIds.LaunchGameVersionFilePath, Level = LogLevel.Information, Message = "GameVersionFilePath: {GameVersionFilePath}")]
    private static partial void LogGameVersionFilePath(ILogger logger, string gameVersionFilePath);

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

    [LoggerMessage(EventId = EventIds.LaunchStartingGame, Level = LogLevel.Information, Message = "Step 3: Launching game (fire-and-forget)...")]
    private static partial void LogStartingGame(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchGameLaunchFailed, Level = LogLevel.Error, Message = "Launch FAILED: {Error}")]
    private static partial void LogGameLaunchFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchLauncherStarted, Level = LogLevel.Information, Message = "Launcher started OK (fire-and-forget, not waiting for exit)")]
    private static partial void LogLauncherStarted(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchEnded, Level = LogLevel.Information, Message = "=== SIMPLIFIED LAUNCH END ===")]
    private static partial void LogLaunchEnded(ILogger logger);
}
