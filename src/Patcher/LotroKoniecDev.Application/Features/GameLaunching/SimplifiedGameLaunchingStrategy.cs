using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class SimplifiedGameLaunchingStrategy : IGameLaunchingStrategy
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
        _logger.LogInformation("=== SIMPLIFIED LAUNCH START ===");
        _logger.LogInformation("DatFilePath: {DatFilePath}", command.DatFilePath);
        _logger.LogInformation("TranslationFilePath: {TranslationFilePath}", command.TranslationFilePath);
        _logger.LogInformation("GameVersionFilePath: {GameVersionFilePath}", command.GameVersionFilePath);

        // 1. Is the game already running?
        if (_gameProcessDetector.IsLotroRunning())
        {
            _logger.LogWarning("BLOCKED: LOTRO already running");
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(DomainErrors.GameLaunch.GameAlreadyRunning));
        }

        // 2. Has the translation file changed since last apply?
        _logger.LogInformation("Step 1: Computing translation file hash...");
        Result<string> hashResult = _fileHasher.ComputeHash(command.TranslationFilePath);
        if (hashResult.IsFailure)
        {
            _logger.LogError("ComputeHash FAILED: {Error}", hashResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(hashResult.Error));
        }
        string currentHash = hashResult.Value;
        
        _logger.LogInformation("Current translation hash: {Hash}", currentHash);

        _logger.LogInformation("Step 2: Reading stored version info...");
        
        Result<StoredVersionInfo?> storedResult = _gameVersionFileStore.ReadStoredVersion(command.GameVersionFilePath);
        if (storedResult.IsFailure)
        {
            _logger.LogError("ReadStoredVersion FAILED: {Error}", storedResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(storedResult.Error));
        }
        StoredVersionInfo? storedInfo = storedResult.Value;

        _logger.LogInformation(
            "Stored info: ForumVersion={Forum}, VnumDat={VnumDat}, VnumGame={VnumGame}, Hash={Hash}",
            storedInfo?.ForumVersion ?? "(null)",
            storedInfo?.VnumDatFile?.ToString() ?? "(null)",
            storedInfo?.VnumGameData?.ToString() ?? "(null)",
            storedInfo?.TranslationFileHash ?? "(null)");

        bool translationChanged = storedResult.Value?.TranslationFileHash is null
            || !string.Equals(currentHash, storedResult.Value.TranslationFileHash, StringComparison.Ordinal);

        _logger.LogInformation("Translation changed? {Changed} (stored hash null={IsNull}, match={Match})",
            translationChanged,
            storedInfo?.TranslationFileHash is null,
            storedInfo?.TranslationFileHash is not null
            && string.Equals(currentHash, storedInfo.TranslationFileHash, StringComparison.Ordinal));

        bool translationsApplied = false;
        int appliedCount = 0;
        int skippedCount = 0;

        if (translationChanged)
        {
            _logger.LogInformation(">>> PATCHING: Translation file changed — applying patch");

            Result<PatchSummaryResponse> patchResult =
                _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
            if (patchResult.IsFailure)
            {
                _logger.LogError("ApplyTranslations FAILED: {Error}", patchResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(
                        DomainErrors.GameLaunch.RepatchFailed(patchResult.Error.Message)));
            }
            
            PatchSummaryResponse patchSummary = patchResult.Value;

            translationsApplied = true;
            appliedCount = patchSummary.AppliedTranslations;
            skippedCount = patchSummary.SkippedTranslations;

            _logger.LogInformation("Patched translations: {Applied} applied, {Skipped} skipped",
                appliedCount, skippedCount);

            // Save new hash + current vnum
            _logger.LogInformation("Reading DAT vnum to save alongside hash...");
            
            Result<DatVersionInfo> vnumResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (vnumResult.IsFailure)
            {
                _logger.LogError("ReadVersion FAILED: {Error}", vnumResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(vnumResult.Error));
            }
            DatVersionInfo datVersion = vnumResult.Value;
            
            _logger.LogInformation("DAT vnum: VnumDat={VnumDat}, VnumGame={VnumGame}",
                datVersion.VnumDatFile, datVersion.VnumGameData);

            Result saveResult = _gameVersionFileStore.SaveVersion(
                command.GameVersionFilePath,
                storedResult.Value?.ForumVersion,
                datVersion.VnumDatFile,
                datVersion.VnumGameData,
                currentHash);
            if (saveResult.IsFailure)
            {
                _logger.LogError("SaveVersion FAILED: {Error}", saveResult.Error.Message);
                return ValueTask.FromResult(
                    Result.Failure<GameLaunchingResponse>(saveResult.Error));
            }
            _logger.LogInformation("Version saved OK");
        }
        else
        {
            _logger.LogInformation(">>> SKIP: Translation file unchanged — skipping patch");
        }

        // 3. Launch (fire-and-forget)
        _logger.LogInformation("Step 3: Launching game (fire-and-forget)...");
        Result launchResult = _gameLauncher.Launch(command.DatFilePath);
        if (launchResult.IsFailure)
        {
            _logger.LogError("Launch FAILED: {Error}", launchResult.Error.Message);
            return ValueTask.FromResult(
                Result.Failure<GameLaunchingResponse>(launchResult.Error));
        }

        _logger.LogInformation("Launcher started OK (fire-and-forget, not waiting for exit)");
        _logger.LogInformation("=== SIMPLIFIED LAUNCH END ===");

        GameLaunchingResponse response = new(
            ForumVersion: null,
            UpdateWasDetected: false,
            GameExitCode: 0,
            TranslationsApplied: translationsApplied,
            AppliedCount: appliedCount,
            SkippedCount: skippedCount);

        return ValueTask.FromResult(Result.Success(response));
    }
}
