using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using Mediator;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
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
    private readonly ILogger<GameLaunchingCommandHandler> _logger;

    public GameLaunchingCommandHandler(
        IGameUpdateChecker gameUpdateChecker,
        IGameVersionFileStore gameVersionFileStore,
        IDatVersionReader datVersionReader,
        IDatFileProtector datFileProtector,
        IGameLauncher gameLauncher,
        IGameProcessDetector gameProcessDetector,
        IPatchingService patchingService,
        ILogger<GameLaunchingCommandHandler> logger)
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

    public async ValueTask<Result<GameLaunchingResponse>> Handle(GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_gameProcessDetector.IsLotroRunning())
        {
            return Result.Failure<GameLaunchingResponse>(DomainErrors.GameLaunch.GameAlreadyRunning);
        }

        // 1. Gather intel: forum version + stored version info
        Result<GameUpdateCheckSummary> checkResult =
            await _gameUpdateChecker.CheckForUpdateAsync(command.GameVersionFilePath);
        if (checkResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(checkResult.Error);
        }

        GameUpdateCheckSummary summary = checkResult.Value;

        // 2. Read current DAT vnum
        Result<DatVersionInfo> currentVnumResult = _datVersionReader.ReadVersion(command.DatFilePath);
        if (currentVnumResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(currentVnumResult.Error);
        }

        DatVersionInfo currentVnum = currentVnumResult.Value;

        // 3. Decision matrix based on forum version + vnum comparison
        bool isFirstRun = summary.IsFirstLaunch;
        bool vnumChanged = !isFirstRun
            && summary.StoredInfo!.VnumGameData is not null
            && currentVnum.VnumGameData != summary.StoredInfo.VnumGameData;
        bool forumNewVersion = summary.ForumVersionChanged;

        // a) First run — no baseline, establish it, re-patch, launch normally
        if (isFirstRun)
        {
            _logger.LogInformation("First run detected — establishing version baseline");
            return await SaveRepatchAndLaunch(command, summary.ForumVersion, currentVnum, cancellationToken);
        }

        // b) Vnum changed — DAT was overwritten by official launcher, re-patch needed
        if (vnumChanged)
        {
            string? forumVersion = summary.ForumVersion ?? summary.StoredInfo!.ForumVersion;
            _logger.LogInformation(
                "DAT vnum changed ({StoredVnum} → {CurrentVnum}) — game was updated, re-patching",
                summary.StoredInfo!.VnumGameData, currentVnum.VnumGameData);
            return await SaveRepatchAndLaunch(command, forumVersion, currentVnum, cancellationToken);
        }

        // c) Forum says new version, but vnum unchanged — game NEEDS updating
        if (forumNewVersion)
        {
            _logger.LogInformation(
                "Forum version changed ({StoredForum} → {ForumVersion}), DAT unchanged — launching updater",
                summary.StoredInfo!.ForumVersion, summary.ForumVersion);
            return await HandleUpdatePath(command, summary.ForumVersion!, currentVnum, cancellationToken);
        }

        // d) No changes — normal launch
        return await ProtectedLaunch(command.DatFilePath, summary.ForumVersion, updateWasDetected: false, cancellationToken);
    }

    /// <summary>
    /// Saves version baseline, re-patches translations, and launches the game.
    /// Used for first run and when DAT was already updated outside the patcher.
    /// </summary>
    private async ValueTask<Result<GameLaunchingResponse>> SaveRepatchAndLaunch(
        GameLaunchingCommand command,
        string? forumVersion,
        DatVersionInfo currentVnum,
        CancellationToken cancellationToken)
    {
        Result saveResult = _gameVersionFileStore.SaveVersion(
            command.GameVersionFilePath, forumVersion, currentVnum.VnumDatFile, currentVnum.VnumGameData);
        if (saveResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(saveResult.Error);
        }

        Result<PatchSummaryResponse> repatchResult =
            _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
        if (repatchResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(
                DomainErrors.GameLaunch.RepatchFailed(repatchResult.Error.Message));
        }

        _logger.LogInformation("Patched translations: {Applied} applied, {Skipped} skipped",
            repatchResult.Value.AppliedTranslations, repatchResult.Value.SkippedTranslations);

        return await ProtectedLaunch(command.DatFilePath, forumVersion, updateWasDetected: true, cancellationToken);
    }

    /// <summary>
    /// Forced update path — the ONLY case where we open the LOTRO launcher for the user.
    /// Forum says there's a new version AND DAT vnum hasn't changed (update not installed yet).
    /// </summary>
    private async ValueTask<Result<GameLaunchingResponse>> HandleUpdatePath(
        GameLaunchingCommand command,
        string forumVersion,
        DatVersionInfo vnumBefore,
        CancellationToken cancellationToken)
    {
        // 1. Unprotect DAT so the launcher CAN update it
        Result unprotectResult = _datFileProtector.Unprotect(command.DatFilePath);
        if (unprotectResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(unprotectResult.Error);
        }

        // DAT is now unprotected — must re-protect on any exit from this point
        try
        {
            // 2. Launch the launcher and wait for our process to exit
            Result<int> launcherResult = await _gameLauncher.LaunchAndWaitForExitAsync(command.DatFilePath, cancellationToken);
            if (launcherResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(launcherResult.Error);
            }

            // 3. Handle UAC restart + kill game client if started
            Result waitResult = await WaitForLauncherCompletionAsync(cancellationToken);
            if (waitResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(waitResult.Error);
            }

            // 4. Read vnum AFTER update to compare with snapshot
            Result<DatVersionInfo> vnumAfterResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (vnumAfterResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(vnumAfterResult.Error);
            }

            DatVersionInfo vnumAfter = vnumAfterResult.Value;

            if (vnumBefore == vnumAfter)
            {
                _logger.LogWarning(
                    "DAT version unchanged after update flow (vnum={Vnum}). " +
                    "User may have closed launcher without updating",
                    vnumAfter.VnumGameData);
            }

            // 5. Save version + re-patch
            Result saveResult = _gameVersionFileStore.SaveVersion(
                command.GameVersionFilePath, forumVersion, vnumAfter.VnumDatFile, vnumAfter.VnumGameData);
            if (saveResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(saveResult.Error);
            }

            Result<PatchSummaryResponse> repatchResult =
                _patchingService.ApplyTranslations(command.TranslationFilePath, command.DatFilePath);
            if (repatchResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(
                    DomainErrors.GameLaunch.RepatchFailed(repatchResult.Error.Message));
            }

            _logger.LogInformation("Re-patched translations after update: {Applied} applied, {Skipped} skipped",
                repatchResult.Value.AppliedTranslations, repatchResult.Value.SkippedTranslations);
        }
        finally
        {
            ProtectBestEffort(command.DatFilePath);
        }

        // 6. Normal launch with fresh translations
        return await ProtectedLaunch(command.DatFilePath, forumVersion, updateWasDetected: true, cancellationToken);
    }

    private async ValueTask<Result<GameLaunchingResponse>> ProtectedLaunch(
        string datFilePath,
        string? forumVersion,
        bool updateWasDetected,
        CancellationToken cancellationToken)
    {
        Result protectResult = _datFileProtector.Protect(datFilePath);
        if (protectResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(protectResult.Error);
        }

        try
        {
            Result<int> launchResult = await _gameLauncher.LaunchAndWaitForExitAsync(datFilePath, cancellationToken);
            if (launchResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(launchResult.Error);
            }

            return Result.Success(new GameLaunchingResponse(
                forumVersion,
                updateWasDetected,
                GameExitCode: launchResult.Value));
        }
        finally
        {
            Result unprotectResult = _datFileProtector.Unprotect(datFilePath);
            if (unprotectResult.IsFailure)
            {
                _logger.LogCritical("Unprotecting DAT file failed after launch. Error: {Error}",
                    unprotectResult.Error.Message);
            }
        }
    }

    /// <summary>
    /// After our started launcher process exits, the launcher may have restarted itself
    /// (UAC elevation for an update). We poll for the restarted launcher to appear,
    /// then wait for it to finish. If the user starts the game client during the update,
    /// we kill everything — they need to finish the update first, then we re-patch and launch properly.
    /// </summary>
    private async Task<Result> WaitForLauncherCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Phase 1: Wait for a restarted launcher to appear (UAC → kill → new process).
            long deadline = Environment.TickCount64 + LauncherReappearTimeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gameProcessDetector.IsLotroLauncherRunning())
                {
                    _logger.LogInformation("Restarted LOTRO launcher detected — monitoring update");
                    break;
                }

                await Task.Delay(ProcessPollingIntervalMs, cancellationToken);
            }

            // Phase 2: Launcher is running (restarted) — wait for it to finish.
            while (_gameProcessDetector.IsLotroLauncherRunning())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gameProcessDetector.IsGameClientRunning())
                {
                    _logger.LogWarning("Game client detected during update — killing LOTRO processes to continue update flow");

                    Result killResult = _gameProcessDetector.KillLotroProcesses();
                    if (killResult.IsFailure)
                    {
                        return Result.Failure(killResult.Error);
                    }

                    break;
                }

                await Task.Delay(ProcessPollingIntervalMs, cancellationToken);
            }

            // Phase 3: Safety net — kill game client if it started while we weren't monitoring.
            // This handles the case where user clicked Play, launcher exited, game is running
            // with locked DAT files that we need to access.
            if (_gameProcessDetector.IsGameClientRunning())
            {
                _logger.LogWarning("Game client running after launcher monitoring — killing to protect DAT file access");

                Result killResult = _gameProcessDetector.KillLotroProcesses();
                if (killResult.IsFailure)
                {
                    return Result.Failure(killResult.Error);
                }
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
