using System.ComponentModel;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The state the main window shows. The values are static until #42 wires the Patch and Play
/// buttons; only the progress bar is already fed, by <see cref="MainWindowProgressReporter"/>.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public const string UnknownValue = "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status { get; } = "Ready to play";

    public string GameVersion { get; } = UnknownValue;

    public string PatchVersion { get; } = UnknownValue;

    public int ProgressPercent
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
        }
    }
}
