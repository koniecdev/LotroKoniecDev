using System.ComponentModel;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The update panel (spec 0016): the check after the main window opens, the question when a newer
/// version exists, and the update after "Tak". A check never takes the busy gate, so it never blocks
/// Patch or Play; the update holds the gate until the app closes.
/// </summary>
/// <remarks>
/// The handlers run on the thread pool, so hashing a 256 MiB installer never freezes the window. The
/// await comes back on the UI thread, which is where the bound properties change.
/// </remarks>
public sealed partial class AppUpdateViewModel : INotifyPropertyChanged
{
    private const string UnexpectedErrorCode = "AppUpdate.Unexpected";

    private readonly IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> _checkHandler;
    private readonly ICommandHandler<InstallAppUpdateCommand, Result> _installHandler;
    private readonly OperationGate _gate;
    private readonly InstalledAppVersion _installedVersion;
    private readonly IAppShutdown _appShutdown;
    private readonly IProgress<OperationProgress> _progress;
    private readonly ILogger<AppUpdateViewModel> _logger;
    private AvailableAppUpdate? _availableUpdate;
    private bool _isInstalling;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The question to the player, or <c>null</c> while there is nothing to offer.</summary>
    public string? PromptText { get; private set; }

    public bool IsPromptVisible => PromptText is not null;

    /// <summary>Why a check or an update failed, or what the update is doing now.</summary>
    public string? StatusText { get; private set; }

    public bool IsStatusVisible => StatusText is not null;

    public DelegateCommand AcceptCommand { get; }

    public DelegateCommand DeclineCommand { get; }

    public async Task CheckAsync()
    {
        Result<AppUpdateCheckResponse> result = await RunCheckAsync();
        if (result.IsFailure)
        {
            ShowStatus(AppUpdateTexts.CheckFailed(result.Error, _installedVersion.Value));
            return;
        }

        if (result.Value is { Outcome: AppUpdateCheckOutcome.UpdateAvailable, Update: { } update })
        {
            _availableUpdate = update;
            ShowPrompt(AppUpdateTexts.Prompt(update.Version));
            RefreshCommands();
        }
        else if (result.Value is { Outcome: AppUpdateCheckOutcome.CheckFailed, Failure: { } failure })
        {
            ShowStatus(AppUpdateTexts.CheckFailed(failure, _installedVersion.Value));
        }
    }

    public async Task AcceptAsync()
    {
        if (_availableUpdate is not { } update || _isInstalling || !_gate.TryEnter())
        {
            return;
        }

        SetInstalling(true);
        ShowStatus(AppUpdateTexts.Downloading(update.Version));

        Result result = await RunInstallAsync(update);

        if (result.IsSuccess)
        {
            // The gate stays taken: nothing may start while the installer replaces the app.
            ShowStatus(AppUpdateTexts.InstallerStarted);
            _appShutdown.Shutdown();
            return;
        }

        // The shared bar would keep the percent the download reached next to the failure text.
        _progress.Report(new OperationProgress(0, 1));
        _gate.Exit();
        SetInstalling(false);
        ShowStatus(AppUpdateTexts.InstallFailed(result.Error, _installedVersion.Value));
    }

    public void Decline()
    {
        if (_isInstalling)
        {
            return;
        }

        _availableUpdate = null;
        ShowPrompt(null);
        ShowStatus(null);
        RefreshCommands();
    }

    public AppUpdateViewModel(
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> checkHandler,
        ICommandHandler<InstallAppUpdateCommand, Result> installHandler,
        OperationGate gate,
        InstalledAppVersion installedVersion,
        IAppShutdown appShutdown,
        IProgress<OperationProgress> progress,
        ILogger<AppUpdateViewModel> logger)
    {
        _checkHandler = checkHandler;
        _installHandler = installHandler;
        _gate = gate;
        _installedVersion = installedVersion;
        _appShutdown = appShutdown;
        _progress = progress;
        _logger = logger;

        AcceptCommand = new DelegateCommand(() => _ = AcceptAsync(), CanAccept);
        DeclineCommand = new DelegateCommand(Decline, () => !_isInstalling);
        _gate.PropertyChanged += (_, _) => RefreshCommands();
    }

    /// <summary>
    /// A handler answers with a <see cref="Result"/>, so an exception here is a bug. It is logged, for
    /// the player's bug report, and shown as a failed check instead of ending the app: Patch and Play
    /// must keep working.
    /// </summary>
    private async Task<Result<AppUpdateCheckResponse>> RunCheckAsync()
    {
        try
        {
            return await Task.Run(async () =>
                await _checkHandler.Handle(new CheckForAppUpdateQuery(_installedVersion.Value), CancellationToken.None));
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            return Result.Failure<AppUpdateCheckResponse>(Unexpected(ex));
        }
    }

    private async Task<Result> RunInstallAsync(AvailableAppUpdate update)
    {
        try
        {
            return await Task.Run(async () => await _installHandler.Handle(
                new InstallAppUpdateCommand(update.Version, update.InstallerUri, update.ChecksumUri),
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            return Result.Failure(Unexpected(ex));
        }
    }

    private static Error Unexpected(Exception ex) => Error.Failure(UnexpectedErrorCode, ex.Message);

    private bool CanAccept() => _availableUpdate is not null && !_isInstalling && !_gate.IsBusy;

    private void SetInstalling(bool isInstalling)
    {
        _isInstalling = isInstalling;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        AcceptCommand.RaiseCanExecuteChanged();
        DeclineCommand.RaiseCanExecuteChanged();
    }

    private void ShowPrompt(string? text)
    {
        PromptText = text;
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(IsPromptVisible));
    }

    private void ShowStatus(string? text)
    {
        StatusText = text;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsStatusVisible));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [LoggerMessage(Level = LogLevel.Error, Message = "The desktop app's update check or update threw instead of returning a result")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception);
}
