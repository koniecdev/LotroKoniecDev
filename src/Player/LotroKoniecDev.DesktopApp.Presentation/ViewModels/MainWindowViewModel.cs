namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The state the main window shows. The texts are static until #42 wires the Patch and Play buttons;
/// only the progress bar is already wired to the progress the patcher handlers report.
/// </summary>
public sealed class MainWindowViewModel
{
    public const string UnknownValue = "—";

    public string Status { get; } = "Ready to play";

    public string GameVersion { get; } = UnknownValue;

    public string PatchVersion { get; } = UnknownValue;

    public ProgressBarViewModel Progress { get; }

    public MainWindowViewModel(ProgressBarViewModel progress)
    {
        Progress = progress;
    }
}
