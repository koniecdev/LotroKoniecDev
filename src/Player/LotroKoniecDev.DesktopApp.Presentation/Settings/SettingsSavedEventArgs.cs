namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

public sealed class SettingsSavedEventArgs : EventArgs
{
    /// <summary>The file now holds the new settings.</summary>
    public bool Written { get; }

    /// <summary>The old file, which the load could not use, was moved aside during this save.</summary>
    public bool FileSetAside { get; }

    public SettingsSavedEventArgs(bool written, bool fileSetAside)
    {
        Written = written;
        FileSetAside = fileSetAside;
    }
}
