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
    private readonly IDatVersionReader _datVersionReader;
    private readonly IDatFileProtector _datFileProtector;
    private readonly IGameLauncher _gameLauncher;
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IPatchingService _patchingService;
    private readonly ILogger<GameLaunchingCommandHandler> _logger;

    public GameLaunchingCommandHandler(
        IGameUpdateChecker gameUpdateChecker,
        IDatVersionReader datVersionReader,
        IDatFileProtector datFileProtector,
        IGameLauncher gameLauncher,
        IGameProcessDetector gameProcessDetector,
        IPatchingService patchingService,
        ILogger<GameLaunchingCommandHandler> logger)
    {
        _gameUpdateChecker = gameUpdateChecker;
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

        Result<GameUpdateCheckSummary> checkResult =
            await _gameUpdateChecker.CheckForUpdateAsync(command.GameVersionFilePath);
        if (checkResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(checkResult.Error);
        }

        GameUpdateCheckSummary summary = checkResult.Value;

        if (summary.UpdateDetected)
        {
            // Invariant: UpdateDetected == true implies ForumVersion != null
            // (CheckForUpdateAsync sets UpdateDetected=false on forum failure)
            string forumVersion = summary.ForumVersion!;

            return await HandleUpdatePath(command, forumVersion, summary.IsFirstLaunch, cancellationToken);
        }

        return await ProtectedLaunch(command.DatFilePath, summary.ForumVersion, updateWasDetected: false, cancellationToken);
    }

    private async ValueTask<Result<GameLaunchingResponse>> HandleUpdatePath(
        GameLaunchingCommand command,
        string forumVersion,
        bool isFirstRun,
        CancellationToken cancellationToken)
    {
        // 1. Snapshot vnum BEFORE update so we can confirm the update actually changed the DAT
        Result<DatVersionInfo> vnumBeforeResult = _datVersionReader.ReadVersion(command.DatFilePath);
        if (vnumBeforeResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(vnumBeforeResult.Error);
        }

        // 2. Unprotect DAT so the launcher CAN update it
        Result unprotectResult = _datFileProtector.Unprotect(command.DatFilePath);
        if (unprotectResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(unprotectResult.Error);
        }

        // DAT is now unprotected — must re-protect on any exit from this point
        try
        {
            // 3. Launch the launcher and wait for our process to exit
            //    The launcher may restart itself (UAC elevation for update) — our process dies,
            //    but a new LotroLauncher.exe appears. We handle that in step 4.
            Result<int> launcherResult = await _gameLauncher.LaunchAndWaitForExitAsync(command.DatFilePath, cancellationToken);
            if (launcherResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(launcherResult.Error);
            }

            // 4. Launcher may have restarted itself — wait for the new process to finish
            //    If the user starts playing (lotroclient) instead of updating, kill everything.
            Result waitResult = await WaitForLauncherCompletionAsync(cancellationToken);
            if (waitResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(waitResult.Error);
            }

            // 5. Read vnum AFTER update to compare with snapshot
            Result<DatVersionInfo> vnumAfterResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (vnumAfterResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(vnumAfterResult.Error);
            }

            // 6. Confirm update and save forum version
            Result confirmResult = _gameUpdateChecker.ConfirmUpdateInstalled(
                command.GameVersionFilePath,
                forumVersion,
                isFirstRun,
                vnumBeforeResult.Value,
                vnumAfterResult.Value);
            if (confirmResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(confirmResult.Error);
            }

            // 7. Re-patch translations on the fresh DAT
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

        // 8. Normal launch with fresh translations
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
            // Poll up to LauncherReappearTimeoutMs. If nothing appears, the launcher
            // just closed normally — no restart happened.
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
            // If the user starts playing instead of updating, kill everything.
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
