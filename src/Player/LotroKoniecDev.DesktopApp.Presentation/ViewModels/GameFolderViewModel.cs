using System.ComponentModel;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The first-run panel (#43). It finds the game when no usable folder is saved, and it asks the player
/// about every folder from a source outside the allow-list (ADR-0069 §2, the #1292 ruling). Only one
/// known install found on a first run is saved with no question. A saved folder that lost its game
/// file is replaced only after the player says so.
/// </summary>
/// <remarks>
/// The disk scan cannot be cancelled, so its result is dropped once the player opens the folder picker
/// or the settings window: a late result must never choose for a player who is already choosing.
/// The settings file and the saved folder are checked on the UI thread before the search starts, so a
/// saved folder on a network share that does not answer holds the window until the share times out.
/// </remarks>
public sealed partial class GameFolderViewModel : INotifyPropertyChanged
{
    private readonly IDatFileLocator _locator;
    private readonly DesktopSettingsStore _store;
    private readonly IPathPicker _picker;
    private readonly ILogger<GameFolderViewModel> _logger;
    private Task? _start;
    private IReadOnlyList<string> _loadNotes = [];
    private string? _lostFolder;
    private DatFileLocation? _offered;
    private bool _isSearching;
    private bool _isScanDropped;
    private bool _isPicking;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPanelVisible { get; private set; }

    public string? PromptText { get; private set; }

    public bool IsQuestionVisible => _offered is not null;

    public IReadOnlyList<GameFolderCandidateViewModel> Candidates { get; private set; } = [];

    public bool IsCandidateListVisible => Candidates.Count > 0;

    /// <summary>What the load dropped from the settings file, or why a save failed.</summary>
    public string? StatusText { get; private set; }

    public bool IsStatusVisible => StatusText is not null;

    public DelegateCommand ConfirmCommand { get; }

    public DelegateCommand DeclineCommand { get; }

    public DelegateCommand ChooseFolderCommand { get; }

    /// <summary>Runs once. A second call returns the first run, so the window's own start and a test can both await it.</summary>
    public Task StartAsync() => _start ??= RunStartAsync();

    public void Confirm()
    {
        if (_offered is { } offered)
        {
            Use(FolderOf(offered));
        }
    }

    /// <summary>Saves nothing: a lost folder stays in the file until the player picks another one.</summary>
    public void Decline()
    {
        if (_offered is null)
        {
            return;
        }

        AskForFolder(HandPrompt());
    }

    /// <summary>A folder the player picks counts like the CLI's <c>-d</c>: chosen, so no question (ADR-0069 §2).</summary>
    public async Task ChooseFolderAsync()
    {
        if (_isPicking)
        {
            return;
        }

        DropPendingScan();
        SetPicking(true);
        try
        {
            string? folder = await _picker.PickGameFolderAsync();
            if (folder is null)
            {
                return;
            }

            if (!_store.ContainsGame(folder))
            {
                AskForFolder(SettingsTexts.NoGameIn(folder));
                return;
            }

            Use(folder);
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            ShowStatus(GameFolderTexts.PickerFailed);
        }
        finally
        {
            SetPicking(false);
        }
    }

    /// <summary>Called when the player starts choosing on their own, in the picker or the settings window.</summary>
    public void DropPendingScan()
    {
        _isScanDropped = true;
        if (!_isSearching)
        {
            return;
        }

        _isSearching = false;
        AskForFolder(HandPrompt());
    }

    internal static string FolderOf(DatFileLocation location) =>
        Path.GetDirectoryName(location.Path) ?? location.Path;

    public GameFolderViewModel(
        IDatFileLocator locator,
        DesktopSettingsStore store,
        IPathPicker picker,
        ILogger<GameFolderViewModel> logger)
    {
        _locator = locator;
        _store = store;
        _picker = picker;
        _logger = logger;

        ConfirmCommand = new DelegateCommand(Confirm, () => _offered is not null);
        DeclineCommand = new DelegateCommand(Decline, () => _offered is not null);
        ChooseFolderCommand = new DelegateCommand(() => _ = ChooseFolderAsync(), () => !_isPicking);
        _store.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// The handlers and the store answer with values, so an exception here is a bug. It is logged, for
    /// the player's bug report, and the panel falls back to the folder button.
    /// </summary>
    private async Task RunStartAsync()
    {
        try
        {
            _loadNotes = _store.Load();
            ShowStatus(null);

            string? saved = _store.Current.GameFolder;
            if (_store.ContainsGame(saved))
            {
                return;
            }

            if (saved is not null)
            {
                _lostFolder = saved;
                LogSavedFolderLost(_logger, saved);
            }

            if (_isScanDropped)
            {
                AskForFolder(HandPrompt());
                return;
            }

            _isSearching = true;
            ShowPanel(GameFolderTexts.Searching, null, []);
            IReadOnlyList<DatFileLocation> locations = await LocateAsync();
            if (_isScanDropped)
            {
                return;
            }

            _isSearching = false;
            Offer(locations);
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            _isSearching = false;
            AskForFolder(GameFolderTexts.NotFound);
        }
    }

    /// <summary>The scan walks every fixed drive when nothing known exists, so it runs off the UI thread.</summary>
    private async Task<IReadOnlyList<DatFileLocation>> LocateAsync()
    {
        try
        {
            Result<IReadOnlyList<DatFileLocation>> result = await Task.Run(() => _locator.LocateAll());
            return result.IsSuccess ? result.Value : [];
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            return [];
        }
    }

    private void Offer(IReadOnlyList<DatFileLocation> locations)
    {
        if (locations.Count == 0)
        {
            AskForFolder(_lostFolder is null ? GameFolderTexts.NotFound : GameFolderTexts.LostAndNotFound(_lostFolder));
            return;
        }

        if (locations.Count > 1)
        {
            ShowPanel(
                _lostFolder is null ? GameFolderTexts.SeveralFound : GameFolderTexts.SeveralFoundInstead(_lostFolder),
                null,
                locations.Select(location => new GameFolderCandidateViewModel(location, Choose)).ToList());
            return;
        }

        DatFileLocation only = locations[0];
        if (_lostFolder is null && !only.NeedsConfirmation)
        {
            Use(FolderOf(only));
            return;
        }

        string prompt = _lostFolder is null
            ? GameFolderTexts.FoundOne(FolderOf(only), only.Source)
            : GameFolderTexts.FoundInstead(_lostFolder, FolderOf(only), only.Source, only.NeedsConfirmation);
        ShowPanel(prompt, only, []);
    }

    private void Choose(GameFolderCandidateViewModel candidate)
    {
        if (Candidates.Contains(candidate))
        {
            Use(candidate.Folder);
        }
    }

    private void Use(string folder)
    {
        bool saved = _store.Save(_store.Current with { GameFolder = folder });
        _lostFolder = null;
        HidePanel();
        ShowStatus(saved ? null : GameFolderTexts.SaveFailed);
    }

    /// <summary>A folder saved in the settings window ends the first run too.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsPanelVisible && _store.ContainsGame(_store.Current.GameFolder))
        {
            _lostFolder = null;
            HidePanel();
        }
    }

    private string HandPrompt() =>
        _lostFolder is null ? GameFolderTexts.ChooseByHand : GameFolderTexts.Lost(_lostFolder);

    private void AskForFolder(string prompt) => ShowPanel(prompt, null, []);

    private void ShowPanel(string prompt, DatFileLocation? offered, IReadOnlyList<GameFolderCandidateViewModel> candidates)
    {
        IsPanelVisible = true;
        PromptText = prompt;
        _offered = offered;
        Candidates = candidates;
        OnPanelChanged();
    }

    private void HidePanel()
    {
        IsPanelVisible = false;
        PromptText = null;
        _offered = null;
        Candidates = [];
        OnPanelChanged();
    }

    /// <summary>The load notes stay on screen next to any later message.</summary>
    private void ShowStatus(string? message)
    {
        IEnumerable<string> lines = message is null ? _loadNotes : _loadNotes.Append(message);
        string text = string.Join(" ", lines);
        StatusText = text.Length == 0 ? null : text;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsStatusVisible));
    }

    private void SetPicking(bool isPicking)
    {
        _isPicking = isPicking;
        ChooseFolderCommand.RaiseCanExecuteChanged();
    }

    private void OnPanelChanged()
    {
        OnPropertyChanged(nameof(IsPanelVisible));
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(IsQuestionVisible));
        OnPropertyChanged(nameof(Candidates));
        OnPropertyChanged(nameof(IsCandidateListVisible));
        ConfirmCommand.RaiseCanExecuteChanged();
        DeclineCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [LoggerMessage(Level = LogLevel.Information, Message = "The saved game folder no longer holds the game file ({Folder}); the app looks for the game again")]
    private static partial void LogSavedFolderLost(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "The desktop app's game-folder search or folder picker threw instead of returning a value")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception);
}
