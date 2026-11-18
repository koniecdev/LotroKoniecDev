using System.ComponentModel;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Constants;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The Patch and Play buttons (#42). Each run takes the busy gate, so it never overlaps an update or a
/// save, and works on the app's own copy of the translation file under <c>data</c>: the download, or
/// the player's own file read as it is, which skips the download (owner decision 3 of 2026-11-18).
/// Patch runs the launch's patch steps without the launch; Play runs the settled launch flow, or only
/// starts the launcher when auto-patch is off, because then it uses no translation file at all.
/// </summary>
/// <remarks>
/// The handlers run on the thread pool, so a patch never freezes the window, and the awaits come back
/// on the UI thread, where the bound properties change. The view model composes the steps itself, as
/// the CLI's commands do (ADR-0033 §3).
/// </remarks>
public sealed partial class PatchAndPlayViewModel : INotifyPropertyChanged
{
    private const string UnexpectedErrorCode = "PatchAndPlay.Unexpected";

    private readonly ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>> _syncHandler;
    private readonly ICommandHandler<PatchIfNeededCommand, Result<PatchIfNeededResponse>> _patchHandler;
    private readonly ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>> _launchHandler;
    private readonly IDesktopPatchWorkspace _workspace;
    private readonly IGameProcessDetector _processDetector;
    private readonly IGameLauncher _launcher;
    private readonly DesktopSettingsStore _store;
    private readonly GameFolderViewModel _gameFolder;
    private readonly OperationGate _gate;
    private readonly ProgressBarViewModel _progress;
    private readonly ILogger<PatchAndPlayViewModel> _logger;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText { get; private set; } = PatchAndPlayTexts.Ready;

    /// <summary>A Patch or Play run is going. An update holds the gate too, but is not a run.</summary>
    public bool IsRunning { get; private set; }

    public DelegateCommand PatchCommand { get; }

    public DelegateCommand PlayCommand { get; }

    public async Task PatchAsync()
    {
        if (!TryBeginRun())
        {
            return;
        }

        try
        {
            DesktopSettings settings = _store.Current;
            if (DatFilePathIn(settings) is not { } datFilePath)
            {
                EndWithFailure(PatchAndPlayTexts.GameFileMissing);
                return;
            }

            ShowStatus(PatchAndPlayTexts.Preparing);
            PreparedTranslationFile prepared = await PrepareTranslationFileAsync(settings, datFilePath);
            if (prepared.FailureText is { } failureText)
            {
                EndWithFailure(failureText);
                return;
            }

            _progress.SetIndeterminate(false);
            ShowStatus(PatchAndPlayTexts.Patching);
            Result<PatchIfNeededResponse> result = await RunAsync(
                "patch",
                () => _patchHandler.Handle(
                    new PatchIfNeededCommand(datFilePath, _workspace.GameVersionFilePath, _workspace.WorkingTranslationFilePath),
                    CancellationToken.None));
            if (result.IsFailure)
            {
                EndWithFailure(PatchAndPlayTexts.ForError(result.Error));
                return;
            }

            string outcome = PatchAndPlayTexts.WithEarlierDownloadNote(
                PatchAndPlayTexts.PatchOutcome(result.Value), prepared.UsedEarlierDownload);
            if (PatchAndPlayTexts.IsFailedPatch(result.Value))
            {
                EndWithFailure(outcome);
                return;
            }

            EndWithSuccess(outcome);
        }
        finally
        {
            EndRun();
        }
    }

    public async Task PlayAsync()
    {
        if (!TryBeginRun())
        {
            return;
        }

        try
        {
            DesktopSettings settings = _store.Current;
            if (DatFilePathIn(settings) is not { } datFilePath)
            {
                EndWithFailure(PatchAndPlayTexts.GameFileMissing);
                return;
            }

            if (!settings.AutoPatchOnLaunch)
            {
                ShowStatus(PatchAndPlayTexts.LaunchingWithoutPatch);
                Result started = await RunAsync("start the launcher", () => StartLauncher(datFilePath));
                if (started.IsFailure)
                {
                    EndWithFailure(PatchAndPlayTexts.ForError(started.Error));
                    return;
                }

                EndWithSuccess(PatchAndPlayTexts.LauncherStarted);
                return;
            }

            ShowStatus(PatchAndPlayTexts.Launching);
            PreparedTranslationFile prepared = await PrepareTranslationFileAsync(settings, datFilePath);
            if (prepared.FailureText is { } failureText)
            {
                EndWithFailure(failureText);
                return;
            }

            Result<GameLaunchingResponse> result = await RunAsync(
                "launch",
                () => _launchHandler.Handle(
                    new GameLaunchingCommand(datFilePath, _workspace.GameVersionFilePath, _workspace.WorkingTranslationFilePath),
                    CancellationToken.None));
            if (result.IsFailure)
            {
                EndWithFailure(PatchAndPlayTexts.ForError(result.Error));
                return;
            }

            EndWithSuccess(PatchAndPlayTexts.WithEarlierDownloadNote(
                PatchAndPlayTexts.LaunchOutcome(result.Value), prepared.UsedEarlierDownload));
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// Closing the last window ends the app, and the end of the app's one scope disposes the game file's
    /// handler, which closes its native handles while the run may still be writing. So the player's own
    /// close waits for the run; a close during an update download is not refused.
    /// </summary>
    /// <returns><c>true</c> when the close must be cancelled.</returns>
    public bool RefuseClose()
    {
        if (!IsRunning)
        {
            return false;
        }

        ShowStatus(PatchAndPlayTexts.CloseRefused);
        return true;
    }

    public PatchAndPlayViewModel(
        ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>> syncHandler,
        ICommandHandler<PatchIfNeededCommand, Result<PatchIfNeededResponse>> patchHandler,
        ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>> launchHandler,
        IDesktopPatchWorkspace workspace,
        IGameProcessDetector processDetector,
        IGameLauncher launcher,
        DesktopSettingsStore store,
        GameFolderViewModel gameFolder,
        OperationGate gate,
        ProgressBarViewModel progress,
        ILogger<PatchAndPlayViewModel> logger)
    {
        _syncHandler = syncHandler;
        _patchHandler = patchHandler;
        _launchHandler = launchHandler;
        _workspace = workspace;
        _processDetector = processDetector;
        _launcher = launcher;
        _store = store;
        _gameFolder = gameFolder;
        _gate = gate;
        _progress = progress;
        _logger = logger;

        PatchCommand = new DelegateCommand(() => _ = PatchAsync(), CanStart);
        PlayCommand = new DelegateCommand(() => _ = PlayAsync(), CanStart);
        _gate.PropertyChanged += (_, _) => RefreshCommands();
        _gameFolder.PropertyChanged += (_, _) => RefreshCommands();
        _store.Saved += (_, _) => RefreshCommands();
    }

    /// <summary>
    /// The source step both buttons share. A download that fails falls back to the last download (spec
    /// 0001 Q5), never to the working copy: that copy may hold an override the player has cleared.
    /// </summary>
    private async Task<PreparedTranslationFile> PrepareTranslationFileAsync(DesktopSettings settings, string datFilePath)
    {
        bool usedEarlierDownload = false;
        if (settings.TranslationFileOverride is { } overridePath)
        {
            Result copied = await RunAsync("copy the player's translation file", () => _workspace.UseTranslationFile(overridePath));
            if (copied.IsFailure)
            {
                return PreparedTranslationFile.Failed(copied.Error.Code == DomainErrors.DesktopPatchWorkspace.SourceNotFoundCode
                    ? PatchAndPlayTexts.OverrideMissing(overridePath)
                    : PatchAndPlayTexts.ForError(copied.Error));
            }
        }
        else
        {
            string downloadPath = _workspace.DownloadedTranslationFilePath;
            Result<TranslationFileSyncResponse> synced = await RunAsync(
                "download the translation file",
                () => _syncHandler.Handle(new SyncTranslationFileCommand(settings.ServerAddress, downloadPath), CancellationToken.None));
            if (synced.IsFailure)
            {
                return PreparedTranslationFile.Failed(PatchAndPlayTexts.ForError(synced.Error));
            }

            LogSyncOutcome(_logger, synced.Value.Outcome, synced.Value.Detail ?? string.Empty);

            Result copied = await RunAsync("copy the downloaded translation file", () => _workspace.UseTranslationFile(downloadPath));
            if (copied.IsFailure)
            {
                return PreparedTranslationFile.Failed(copied.Error.Code != DomainErrors.DesktopPatchWorkspace.SourceNotFoundCode
                    ? PatchAndPlayTexts.ForError(copied.Error)
                    : synced.Value.Outcome == TranslationFileSyncOutcome.UpToDate
                        ? PatchAndPlayTexts.DownloadMissing(downloadPath)
                        : PatchAndPlayTexts.NoDownloadYet);
            }

            usedEarlierDownload = synced.Value.Outcome is TranslationFileSyncOutcome.OfflineUsedCache
                or TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache
                or TranslationFileSyncOutcome.EndpointUnresolvedUsedCache;
        }

        Result reset = await RunAsync("check which game file was patched", () => _workspace.ResetPatchStateIfGameFileChanged(datFilePath));
        return reset.IsFailure
            ? PreparedTranslationFile.Failed(PatchAndPlayTexts.ForError(reset.Error))
            : new PreparedTranslationFile(null, usedEarlierDownload);
    }

    private Result StartLauncher(string datFilePath) =>
        _processDetector.IsLotroRunning()
            ? Result.Failure(DomainErrors.GameLaunch.GameAlreadyRunning)
            : _launcher.Launch(datFilePath);

    /// <summary>
    /// A handler or port answers with a <see cref="Result"/>, so an exception here is a bug. It is
    /// logged for the player's bug report and shown as a failure, and the app keeps running.
    /// </summary>
    private async Task<Result<T>> RunAsync<T>(string step, Func<ValueTask<Result<T>>> work)
    {
        Result<T> result;
        try
        {
            result = await Task.Run(async () => await work());
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, step, ex);
            return Result.Failure<T>(Unexpected(ex));
        }

        if (result.IsFailure)
        {
            LogStepFailed(_logger, step, result.Error.Code, result.Error.Message);
        }

        return result;
    }

    private async Task<Result> RunAsync(string step, Func<Result> work)
    {
        Result result;
        try
        {
            result = await Task.Run(work);
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, step, ex);
            return Result.Failure(Unexpected(ex));
        }

        if (result.IsFailure)
        {
            LogStepFailed(_logger, step, result.Error.Code, result.Error.Message);
        }

        return result;
    }

    private static Error Unexpected(Exception ex) => Error.Failure(UnexpectedErrorCode, ex.Message);

    private string? DatFilePathIn(DesktopSettings settings) =>
        settings.GameFolder is { } folder && _store.ContainsGame(folder)
            ? Path.Combine(folder, DatFileConstants.DatFileName)
            : null;

    private bool CanStart() =>
        !IsRunning
        && !_gate.IsBusy
        && !_gameFolder.IsPanelVisible
        && _store.Current.GameFolder is not null;

    /// <summary>The bar starts at 0 with no percent: the first steps send no reports.</summary>
    private bool TryBeginRun()
    {
        if (!CanStart() || !_gate.TryEnter())
        {
            return false;
        }

        SetRunning(true);
        _progress.Report(new OperationProgress(0, 1));
        _progress.SetIndeterminate(true);
        return true;
    }

    /// <summary>A failed run leaves the bar at 0, so no percent sits next to the failure text.</summary>
    private void EndWithFailure(string text)
    {
        _progress.Report(new OperationProgress(0, 1));
        ShowStatus(text);
    }

    /// <summary>The patcher sends no final report, so the bar is set to 100 here (#41 review note 1).</summary>
    private void EndWithSuccess(string text)
    {
        _progress.Report(new OperationProgress(1, 1));
        ShowStatus(text);
    }

    private void EndRun()
    {
        _progress.SetIndeterminate(false);
        _gate.Exit();
        SetRunning(false);
    }

    private void SetRunning(bool isRunning)
    {
        IsRunning = isRunning;
        OnPropertyChanged(nameof(IsRunning));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        PatchCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
    }

    private void ShowStatus(string text)
    {
        StatusText = text;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [LoggerMessage(Level = LogLevel.Information, Message = "Translation file sync: {Outcome} {Detail}")]
    private static partial void LogSyncOutcome(ILogger logger, TranslationFileSyncOutcome outcome, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app's step '{Step}' failed: {Code} {Message}")]
    private static partial void LogStepFailed(ILogger logger, string step, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "The desktop app's step '{Step}' threw instead of returning a result")]
    private static partial void LogUnexpectedFailure(ILogger logger, string step, Exception exception);

    /// <param name="FailureText">The Polish text to show when the step failed, or <c>null</c> when the working copy is ready.</param>
    private sealed record PreparedTranslationFile(string? FailureText, bool UsedEarlierDownload)
    {
        public static PreparedTranslationFile Failed(string failureText) => new(failureText, false);
    }
}
