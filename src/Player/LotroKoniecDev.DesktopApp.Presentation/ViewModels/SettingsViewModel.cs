using System.ComponentModel;
using System.Runtime.CompilerServices;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.Primitives.Constants;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The settings window (#43). Save refuses a value the app could not use, so the file only ever holds
/// what the store accepts on the next start. A folder typed or picked here counts as the player's
/// choice, like the CLI's <c>-d</c> (ADR-0069 §2).
/// </summary>
public sealed partial class SettingsViewModel : INotifyPropertyChanged
{
    private readonly DesktopSettingsStore _store;
    private readonly IPathPicker _picker;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _isPicking;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a successful save or a cancel; the window closes itself.</summary>
    public event EventHandler? CloseRequested;

    public string GameFolder
    {
        get;
        set
        {
            SetField(ref field, value);
        }
    } = string.Empty;

    public string TranslationFile
    {
        get;
        set
        {
            SetField(ref field, value);
        }
    } = string.Empty;

    public bool AutoPatchOnLaunch
    {
        get;
        set
        {
            SetField(ref field, value);
        }
    }

    /// <summary>An empty field means the default server: the player never has to type an address (wiki: Wersje systemu).</summary>
    public string ServerAddress
    {
        get;
        set
        {
            SetField(ref field, value);
        }
    } = string.Empty;

    public string? ErrorText { get; private set; }

    public bool IsErrorVisible => ErrorText is not null;

    public DelegateCommand BrowseGameFolderCommand { get; }

    public DelegateCommand BrowseTranslationFileCommand { get; }

    public DelegateCommand SaveCommand { get; }

    public DelegateCommand CancelCommand { get; }

    /// <summary>Fills the fields from the settings in use, each time the window opens.</summary>
    public void BeginEdit()
    {
        DesktopSettings current = _store.Current;
        GameFolder = current.GameFolder ?? string.Empty;
        TranslationFile = current.TranslationFileOverride ?? string.Empty;
        AutoPatchOnLaunch = current.AutoPatchOnLaunch;
        ServerAddress = current.ServerAddress;
        ShowError(null);
    }

    /// <returns><c>true</c> when the settings were saved and the window may close.</returns>
    public bool Save()
    {
        string folder = GameFolder.Trim();
        string translationFile = TranslationFile.Trim();
        string address = ServerAddress.Trim();

        if (!_store.ContainsGame(folder))
        {
            ShowError(folder.Length == 0 ? SettingsTexts.NoGameFolder : SettingsTexts.NoGameIn(folder));
            return false;
        }

        if (translationFile.Length > 0 && !_store.TranslationFileExists(translationFile))
        {
            ShowError(SettingsTexts.TranslationFileMissingOnSave);
            return false;
        }

        if (address.Length == 0)
        {
            address = TmsConstants.DefaultBaseUrl;
        }
        else if (!TranslationServerAddress.IsAllowed(address))
        {
            ShowError(SettingsTexts.ServerAddressRefused);
            return false;
        }

        bool saved = _store.Save(new DesktopSettings(
            folder,
            translationFile.Length == 0 ? null : translationFile,
            AutoPatchOnLaunch,
            address));
        if (!saved)
        {
            ShowError(SettingsTexts.SaveFailed);
            return false;
        }

        ShowError(null);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public Task BrowseGameFolderAsync() => BrowseAsync(_picker.PickGameFolderAsync, path => GameFolder = path);

    public Task BrowseTranslationFileAsync() => BrowseAsync(_picker.PickTranslationFileAsync, path => TranslationFile = path);

    public SettingsViewModel(DesktopSettingsStore store, IPathPicker picker, ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _picker = picker;
        _logger = logger;

        BrowseGameFolderCommand = new DelegateCommand(() => _ = BrowseGameFolderAsync(), () => !_isPicking);
        BrowseTranslationFileCommand = new DelegateCommand(() => _ = BrowseTranslationFileAsync(), () => !_isPicking);
        SaveCommand = new DelegateCommand(() => Save(), () => !_isPicking);
        CancelCommand = new DelegateCommand(Cancel, () => true);
    }

    /// <summary>A cancelled dialog leaves the field as it was.</summary>
    private async Task BrowseAsync(Func<Task<string?>> pick, Action<string> apply)
    {
        if (_isPicking)
        {
            return;
        }

        SetPicking(true);
        try
        {
            string? path = await pick();
            if (path is not null)
            {
                apply(path);
            }
        }
        catch (Exception ex)
        {
            LogUnexpectedFailure(_logger, ex);
            ShowError(SettingsTexts.PickerFailed);
        }
        finally
        {
            SetPicking(false);
        }
    }

    private void SetPicking(bool isPicking)
    {
        _isPicking = isPicking;
        BrowseGameFolderCommand.RaiseCanExecuteChanged();
        BrowseTranslationFileCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void ShowError(string? text)
    {
        ErrorText = text;
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsErrorVisible));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [LoggerMessage(Level = LogLevel.Error, Message = "The settings window's file or folder picker threw instead of returning a path")]
    private static partial void LogUnexpectedFailure(ILogger logger, Exception exception);
}
