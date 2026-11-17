namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <param name="written">The file now holds the new settings.</param>
/// <param name="fileSetAside">The old file, which the load could not use, was moved aside during this save.</param>
public sealed class SettingsSavedEventArgs : EventArgs
{
    public bool Written { get; }

    public bool FileSetAside { get; }

    public SettingsSavedEventArgs(bool written, bool fileSetAside)
    {
        Written = written;
        FileSetAside = fileSetAside;
    }
}
