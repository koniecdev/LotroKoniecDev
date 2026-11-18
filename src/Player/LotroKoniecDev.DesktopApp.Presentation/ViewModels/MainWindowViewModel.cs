namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The state the main window shows. The two version lines stay unknown until a later ticket reads the
/// real values.
/// </summary>
public sealed class MainWindowViewModel
{
    public const string UnknownValue = "—";

    public string GameVersion { get; } = UnknownValue;

    public string PatchVersion { get; } = UnknownValue;

    public string RiskNotice { get; } = PlayerNotices.Risk;

    public string LauncherNotice { get; } = PlayerNotices.Launcher;

    public ProgressBarViewModel Progress { get; }

    public GameFolderViewModel GameFolder { get; }

    public SettingsViewModel Settings { get; }

    public AppUpdateViewModel AppUpdate { get; }

    public PatchAndPlayViewModel PatchAndPlay { get; }

    /// <summary>The player is choosing now, so a disk scan that is still running must not choose for them.</summary>
    public SettingsViewModel OpenSettings()
    {
        GameFolder.DropPendingScan();
        Settings.BeginEdit();
        return Settings;
    }

    public MainWindowViewModel(
        ProgressBarViewModel progress,
        GameFolderViewModel gameFolder,
        SettingsViewModel settings,
        AppUpdateViewModel appUpdate,
        PatchAndPlayViewModel patchAndPlay)
    {
        Progress = progress;
        GameFolder = gameFolder;
        Settings = settings;
        AppUpdate = appUpdate;
        PatchAndPlay = patchAndPlay;
    }
}
