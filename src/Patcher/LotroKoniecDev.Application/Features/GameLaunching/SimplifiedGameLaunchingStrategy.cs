using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Patching;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.GameLaunching;

/// <summary>
/// The settled launch flow: the patch steps <see cref="IPatchIfNeededService"/> runs, then a start of
/// the launcher that does not wait for the game.
/// </summary>
internal sealed partial class SimplifiedGameLaunchingStrategy : IGameLaunchingStrategy
{
    private readonly IPatchIfNeededService _patchIfNeededService;
    private readonly IGameLauncher _gameLauncher;
    private readonly ILogger<SimplifiedGameLaunchingStrategy> _logger;

    public SimplifiedGameLaunchingStrategy(
        IPatchIfNeededService patchIfNeededService,
        IGameLauncher gameLauncher,
        ILogger<SimplifiedGameLaunchingStrategy> logger)
    {
        _patchIfNeededService = patchIfNeededService;
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

        // No progress: the CLI's progress port prints a line per report, and `launch` never printed any.
        Result<PatchIfNeededResponse> patchResult = _patchIfNeededService.PatchIfNeeded(
            command.DatFilePath, command.GameVersionFilePath, command.TranslationFilePath);
        if (patchResult.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<GameLaunchingResponse>(patchResult.Error));
        }

        PatchIfNeededResponse patch = patchResult.Value;

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
            TranslationsApplied: patch.TranslationsApplied,
            AppliedCount: patch.AppliedCount,
            SkippedCount: patch.SkippedCount,
            WipedSubFileCount: patch.WipedSubFileCount,
            RepairFailure: patch.RepairFailure);

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

    [LoggerMessage(EventId = EventIds.LaunchStartingGame, Level = LogLevel.Information, Message = "Step 3: Launching game (fire-and-forget)...")]
    private static partial void LogStartingGame(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchGameLaunchFailed, Level = LogLevel.Error, Message = "Launch FAILED: {Error}")]
    private static partial void LogGameLaunchFailed(ILogger logger, string error);

    [LoggerMessage(EventId = EventIds.LaunchLauncherStarted, Level = LogLevel.Information, Message = "Launcher started OK (fire-and-forget, not waiting for exit)")]
    private static partial void LogLauncherStarted(ILogger logger);

    [LoggerMessage(EventId = EventIds.LaunchEnded, Level = LogLevel.Information, Message = "=== SIMPLIFIED LAUNCH END ===")]
    private static partial void LogLaunchEnded(ILogger logger);
}
