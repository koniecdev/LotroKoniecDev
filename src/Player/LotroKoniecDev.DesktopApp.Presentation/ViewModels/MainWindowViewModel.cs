namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The state the main window shows. The status texts are static until #42 wires the Patch and Play
/// buttons; the progress bar, the first-run panel and the settings are already wired.
/// </summary>
public sealed class MainWindowViewModel
{
    public const string UnknownValue = "—";

    public string Status { get; } = "Ready to play";

    public string GameVersion { get; } = UnknownValue;

    public string PatchVersion { get; } = UnknownValue;

    public string RiskNotice { get; } = PlayerNotices.Risk;

    public string LauncherNotice { get; } = PlayerNotices.Launcher;

    public ProgressBarViewModel Progress { get; }

    public GameFolderViewModel GameFolder { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>The player is choosing now, so a disk scan that is still running must not choose for them.</summary>
    public SettingsViewModel OpenSettings()
    {
        GameFolder.DropPendingScan();
        Settings.BeginEdit();
        return Settings;
    }

    public MainWindowViewModel(ProgressBarViewModel progress, GameFolderViewModel gameFolder, SettingsViewModel settings)
    {
        Progress = progress;
        GameFolder = gameFolder;
        Settings = settings;
    }
}
