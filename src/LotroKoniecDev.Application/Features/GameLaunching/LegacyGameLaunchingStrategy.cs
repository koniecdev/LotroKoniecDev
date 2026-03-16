using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class LegacyGameLaunchingStrategy : IGameLaunchingStrategy
{
    private const int ProcessPollingIntervalMs = 1000;
    private const int LauncherReappearTimeoutMs = 15000;

    private readonly IGameUpdateChecker _gameUpdateChecker;
    private readonly IGameVersionFileStore _gameVersionFileStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IDatFileProtector _datFileProtector;
    private readonly IGameLauncher _gameLauncher;
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IPatchingService _patchingService;
    private readonly ILogger<LegacyGameLaunchingStrategy> _logger;

    public LegacyGameLaunchingStrategy(
        IGameUpdateChecker gameUpdateChecker,
        IGameVersionFileStore gameVersionFileStore,
        IDatVersionReader datVersionReader,
        IDatFileProtector datFileProtector,
        IGameLauncher gameLauncher,
        IGameProcessDetector gameProcessDetector,
        IPatchingService patchingService,
        ILogger<LegacyGameLaunchingStrategy> logger)
    {
        _gameUpdateChecker = gameUpdateChecker;
        _gameVersionFileStore = gameVersionFileStore;
        _datVersionReader = datVersionReader;
        _datFileProtector = datFileProtector;
        _gameLauncher = gameLauncher;
        _gameProcessDetector = gameProcessDetector;
        _patchingService = patchingService;
        _logger = logger;
    }

    public async ValueTask<Result<GameLaunchingResponse>> ExecuteAsync(
        GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== LEGACY LAUNCH START ===");
        _logger.LogInformation("DatFilePath: {DatFilePath}", command.DatFilePath);
        _logger.LogInformation("TranslationFilePath: {TranslationFilePath}", command.TranslationFilePath);
        _logger.LogInformation("GameVersionFilePath: {GameVersionFilePath}", command.GameVersionFilePath);

        if (_gameProcessDetector.IsLotroRunning())
        {
            _logger.LogWarning("BLOCKED: LOTRO already running");
            return Result.Failure<GameLaunchingResponse>(DomainErrors.GameLaunch.GameAlreadyRunning);
        }

        // 1. Gather intel: forum version + stored version info
        _logger.LogInformation("Step 1: Checking for game update (forum + stored version)...");
        
        Result<GameUpdateCheckSummary> checkResult =
            await _gameUpdateChecker.CheckForUpdateAsync(command.GameVersionFilePath);
        if (checkResult.IsFailure)
        {
            _logger.LogError("CheckForUpdate FAILED: {Error}", checkResult.Error.Message);
            return Result.Failure<GameLaunchingResponse>(checkResult.Error);
        }

        GameUpdateCheckSummary summary = checkResult.Value;
        
        _logger.LogInformation("Forum version: {ForumVersion}", summary.ForumVersion ?? "(null)");
        _logger.LogInformation("Stored info: ForumVersion={StoredForum}, VnumDat={VnumDat}, VnumGame={VnumGame}",
            summary.StoredInfo?.ForumVersion ?? "(null)",
            summary.StoredInfo?.VnumDatFile?.ToString() ?? "(null)",
            summary.StoredInfo?.VnumGameData?.ToString() ?? "(null)");
        _logger.LogInformation("IsFirstLaunch={IsFirst}, ForumVersionChanged={ForumChanged}",
            summary.IsFirstLaunch, summary.ForumVersionChanged);

        // 2. Read current DAT vnum
        _logger.LogInformation("Step 2: Reading current DAT vnum...");
        
        Result<DatVersionInfo> currentVnumResult = _datVersionReader.ReadVersion(command.DatFilePath);
        if (currentVnumResult.IsFailure)
        {
            _logger.LogError("ReadVersion FAILED: {Error}", currentVnumResult.Error.Message);
            return Result.Failure<GameLaunchingResponse>(currentVnumResult.Error);
        }

        DatVersionInfo currentVnum = currentVnumResult.Value;
        
        _logger.LogInformation("Current DAT: VnumDatFile={VnumDat}, VnumGameData={VnumGame}",
            currentVnum.VnumDatFile, currentVnum.VnumGameData);

        // 3. Decision matrix based on forum version + vnum comparison
        bool isFirstRun = summary.IsFirstLaunch;
        bool vnumChanged = !isFirstRun
            && summary.StoredInfo!.VnumGameData is not null
            && currentVnum.VnumGameData != summary.StoredInfo.VnumGameData;
        bool forumNewVersion = summary.ForumVersionChanged;

        _logger.LogInformation(
            "Decision matrix: isFirstRun={IsFirst}, vnumChanged={VnumChanged}, forumNewVersion={ForumNew}",
            isFirstRun, vnumChanged, forumNewVersion);

        // a) First run — no baseline, establish it, re-patch, launch normally
        if (isFirstRun)
        {
            _logger.LogInformation(">>> BRANCH A: First run — establishing version baseline");
            Result<GameLaunchingResponse> result = await SaveRepatchAndLaunch(
                command,
                summary.ForumVersion,
                currentVnum,
                cancellationToken);
            return result;
        }

        // b) Vnum changed — DAT was overwritten by official launcher, re-patch needed
        if (vnumChanged)
        {
            string? forumVersion = summary.ForumVersion ?? summary.StoredInfo!.ForumVersion;
            _logger.LogInformation(
                ">>> BRANCH B: DAT vnum changed ({StoredVnum} → {CurrentVnum}) — re-patching",
                summary.StoredInfo!.VnumGameData, currentVnum.VnumGameData);
            return await SaveRepatchAndLaunch(command, forumVersion, currentVnum, cancellationToken);
        }

        // c) Forum says new version, but vnum unchanged — game NEEDS updating
        if (forumNewVersion)
        {
            _logger.LogInformation(
                ">>> BRANCH C: Forum version changed ({StoredForum} → {ForumVersion}), DAT unchanged — UPDATE PATH",
                summary.StoredInfo!.ForumVersion, summary.ForumVersion);
            return await HandleUpdatePath(command, summary.ForumVersion!, currentVnum, cancellationToken);
        }

        // d) No changes — normal launch
        _logger.LogInformation(">>> BRANCH D: No changes — normal protected launch");
        return await ProtectedLaunch(command.DatFilePath, summary.ForumVersion, updateWasDetected: false, cancellationToken);
    }

    private async ValueTask<Result<GameLaunchingResponse>> SaveRepatchAndLaunch(
        GameLaunchingCommand command,
        string? forumVersion,
        DatVersionInfo currentVnum,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "SaveRepatchAndLaunch: saving version (forum={Forum}, vnumDat={VnumDat}, vnumGame={VnumGame})",
            forumVersion ?? "(null)",
            currentVnum.VnumDatFile,
            currentVnum.VnumGameData);

        Result saveResult = _gameVersionFileStore.SaveVersion(
            versionFilePath: command.GameVersionFilePath,
            forumVersion: forumVersion,
            vnumDatFile: currentVnum.VnumDatFile,
            vnumGameData: currentVnum.VnumGameData);
        if (saveResult.IsFailure)
        {
            _logger.LogError("SaveVersion FAILED: {Error}", saveResult.Error.Message);
            return Result.Failure<GameLaunchingResponse>(saveResult.Error);
        }

        _logger.LogInformation("SaveRepatchAndLaunch: applying translations from {Path}", command.TranslationFilePath);
        
        Result<PatchSummaryResponse> repatchResult =
            _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
        if (repatchResult.IsFailure)
        {
            _logger.LogError("ApplyTranslations FAILED: {Error}", repatchResult.Error.Message);
            
            return Result.Failure<GameLaunchingResponse>(
                DomainErrors.GameLaunch.RepatchFailed(repatchResult.Error.Message));
        }

        _logger.LogInformation("Patched translations: {Applied} applied, {Skipped} skipped",
            repatchResult.Value.AppliedTranslations, repatchResult.Value.SkippedTranslations);

        Result<GameLaunchingResponse> result = await ProtectedLaunch(
            datFilePath: command.DatFilePath,
            forumVersion: forumVersion,
            updateWasDetected: true,
            cancellationToken: cancellationToken);
        return result;
    }

    private async ValueTask<Result<GameLaunchingResponse>> HandleUpdatePath(
        GameLaunchingCommand command,
        string forumVersion,
        DatVersionInfo vnumBefore,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== UPDATE PATH START ===");
        _logger.LogInformation("Vnum BEFORE update: VnumDat={VnumDat}, VnumGame={VnumGame}",
            vnumBefore.VnumDatFile, vnumBefore.VnumGameData);

        // 1. Unprotect DAT so the launcher CAN update it
        _logger.LogInformation("Step U1: Unprotecting DAT file...");
        Result unprotectResult = _datFileProtector.Unprotect(command.DatFilePath);
        if (unprotectResult.IsFailure)
        {
            _logger.LogError("Unprotect FAILED: {Error}", unprotectResult.Error.Message);
            return Result.Failure<GameLaunchingResponse>(unprotectResult.Error);
        }
        _logger.LogInformation("DAT unprotected OK");

        // DAT is now unprotected — must re-protect on any exit from this point
        try
        {
            // 2. Launch the launcher and wait for our process to exit
            _logger.LogInformation("Step U2: Launching game launcher (will wait for exit)...");
            Result<int> launcherResult = await _gameLauncher.LaunchAndWaitForExitAsync(command.DatFilePath, cancellationToken);
            if (launcherResult.IsFailure)
            {
                _logger.LogError("LaunchAndWaitForExit FAILED: {Error}", launcherResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(launcherResult.Error);
            }
            _logger.LogInformation("Launcher initial process exited with code {ExitCode}", launcherResult.Value);

            // 3. Handle UAC restart + kill game client if started
            _logger.LogInformation("Step U3: Waiting for UAC-restarted launcher...");
            Result waitResult = await WaitForLauncherCompletionAsync(cancellationToken);
            if (waitResult.IsFailure)
            {
                _logger.LogError("WaitForLauncher FAILED: {Error}", waitResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(waitResult.Error);
            }
            _logger.LogInformation("Launcher monitoring complete");

            // 4. Read vnum AFTER update to compare with snapshot
            _logger.LogInformation("Step U4: Reading DAT vnum AFTER update...");
            Result<DatVersionInfo> vnumAfterResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (vnumAfterResult.IsFailure)
            {
                _logger.LogError("ReadVersion after update FAILED: {Error}", vnumAfterResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(vnumAfterResult.Error);
            }

            DatVersionInfo vnumAfter = vnumAfterResult.Value;
            _logger.LogInformation("Vnum AFTER update: VnumDat={VnumDat}, VnumGame={VnumGame}",
                vnumAfter.VnumDatFile, vnumAfter.VnumGameData);

            if (vnumBefore == vnumAfter)
            {
                _logger.LogWarning(
                    "!!! DAT version UNCHANGED after update flow (vnum={Vnum}). " +
                    "User may have closed launcher without updating",
                    vnumAfter.VnumGameData);
            }
            else
            {
                _logger.LogInformation("DAT version CHANGED: {Before} → {After}",
                    vnumBefore.VnumGameData, vnumAfter.VnumGameData);
            }

            // 5. Save version + re-patch
            _logger.LogInformation("Step U5: Saving new version + re-patching translations...");
            Result saveResult = _gameVersionFileStore.SaveVersion(
                command.GameVersionFilePath, forumVersion, vnumAfter.VnumDatFile, vnumAfter.VnumGameData);
            if (saveResult.IsFailure)
            {
                _logger.LogError("SaveVersion FAILED: {Error}", saveResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(saveResult.Error);
            }

            Result<PatchSummaryResponse> repatchResult =
                _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
            if (repatchResult.IsFailure)
            {
                _logger.LogError("ApplyTranslations after update FAILED: {Error}", repatchResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(
                    DomainErrors.GameLaunch.RepatchFailed(repatchResult.Error.Message));
            }

            _logger.LogInformation("Re-patched translations after update: {Applied} applied, {Skipped} skipped",
                repatchResult.Value.AppliedTranslations, repatchResult.Value.SkippedTranslations);
        }
        finally
        {
            _logger.LogInformation("Finally: re-protecting DAT...");
            ProtectBestEffort(command.DatFilePath);
        }

        // 6. Normal launch with fresh translations
        _logger.LogInformation("Step U6: Launching game with fresh translations (protected)...");
        return await ProtectedLaunch(command.DatFilePath, forumVersion, updateWasDetected: true, cancellationToken);
    }

    private async ValueTask<Result<GameLaunchingResponse>> ProtectedLaunch(
        string datFilePath,
        string? forumVersion,
        bool updateWasDetected,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProtectedLaunch: protecting DAT...");
        Result protectResult = _datFileProtector.Protect(datFilePath);
        if (protectResult.IsFailure)
        {
            _logger.LogError("Protect FAILED: {Error}", protectResult.Error.Message);
            return Result.Failure<GameLaunchingResponse>(protectResult.Error);
        }
        _logger.LogInformation("DAT protected. Launching game (will wait for exit)...");

        try
        {
            Result<int> launchResult = await _gameLauncher.LaunchAndWaitForExitAsync(datFilePath, cancellationToken);
            if (launchResult.IsFailure)
            {
                _logger.LogError("ProtectedLaunch FAILED: {Error}", launchResult.Error.Message);
                return Result.Failure<GameLaunchingResponse>(launchResult.Error);
            }

            _logger.LogInformation("Game session ended. ExitCode={ExitCode}, UpdateDetected={Update}",
                launchResult.Value, updateWasDetected);
            _logger.LogInformation("=== LEGACY LAUNCH END ===");

            return Result.Success(new GameLaunchingResponse(
                forumVersion,
                updateWasDetected,
                GameExitCode: launchResult.Value));
        }
        finally
        {
            _logger.LogInformation("Finally: unprotecting DAT after game session...");
            Result unprotectResult = _datFileProtector.Unprotect(datFilePath);
            if (unprotectResult.IsFailure)
            {
                _logger.LogCritical("Unprotecting DAT file failed after launch. Error: {Error}",
                    unprotectResult.Error.Message);
            }
            else
            {
                _logger.LogInformation("DAT unprotected OK");
            }
        }
    }

    private async Task<Result> WaitForLauncherCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Phase 1: Wait for a restarted launcher to appear (UAC → kill → new process).
            _logger.LogInformation(
                "Phase 1: Waiting for UAC-restarted launcher (timeout={Timeout}ms)...", 
                LauncherReappearTimeoutMs);
            
            long deadline = Environment.TickCount64 + LauncherReappearTimeoutMs;
            bool launcherReappeared = false;

            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gameProcessDetector.IsLotroLauncherRunning())
                {
                    _logger.LogInformation("Phase 1: Restarted LOTRO launcher detected!");
                    
                    launcherReappeared = true;
                    break;
                }

                await Task.Delay(ProcessPollingIntervalMs, cancellationToken);
            }

            if (!launcherReappeared)
            {
                _logger.LogWarning("Phase 1: Launcher did NOT reappear within timeout — proceeding anyway");
            }

            // Phase 2: Launcher is running (restarted) — wait for it to finish.
            _logger.LogInformation("Phase 2: Monitoring launcher until exit...");
            int pollCount = 0;
            while (_gameProcessDetector.IsLotroLauncherRunning())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pollCount++;

                if (pollCount % 10 == 0)
                {
                    _logger.LogDebug("Phase 2: Still waiting for launcher to exit (poll #{Poll})...", pollCount);
                }

                if (_gameProcessDetector.IsGameClientRunning())
                {
                    _logger.LogWarning("Phase 2: Game client detected during update — killing LOTRO processes");

                    Result killResult = _gameProcessDetector.KillLotroProcesses();
                    if (killResult.IsFailure)
                    {
                        _logger.LogError("KillLotroProcesses FAILED: {Error}", killResult.Error.Message);
                        return Result.Failure(killResult.Error);
                    }

                    _logger.LogInformation("Phase 2: Processes killed OK");
                    break;
                }

                await Task.Delay(ProcessPollingIntervalMs, cancellationToken);
            }
            
            _logger.LogInformation("Phase 2: Launcher monitoring ended (polled {Count} times)", pollCount);

            // Phase 3: Safety net — kill game client if it started while we weren't monitoring.
            if (_gameProcessDetector.IsGameClientRunning())
            {
                _logger.LogWarning("Phase 3: Game client still running — killing");

                Result killResult = _gameProcessDetector.KillLotroProcesses();
                if (killResult.IsFailure)
                {
                    _logger.LogError("KillLotroProcesses FAILED: {Error}", killResult.Error.Message);
                    return Result.Failure(killResult.Error);
                }
                _logger.LogInformation("Phase 3: Processes killed OK");
            }
            else
            {
                _logger.LogInformation("Phase 3: No game client running — clean exit");
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Launcher monitoring was cancelled");
            return Result.Failure(DomainErrors.GameLaunch.LaunchFailed("Operation was cancelled while waiting for launcher."));
        }
    }

    private void ProtectBestEffort(string datFilePath)
    {
        Result result = _datFileProtector.Protect(datFilePath);
        if (result.IsFailure)
        {
            _logger.LogCritical("Failed to re-protect DAT after failure: {Error}", result.Error.Message);
        }
    }
}
