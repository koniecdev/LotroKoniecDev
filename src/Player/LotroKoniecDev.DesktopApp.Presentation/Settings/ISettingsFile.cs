namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <summary>
/// The app's <c>config.json</c>. The exe gives the real file in the player's AppData folder, so the
/// UI tests stay pure. Read and write failures surface as <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>.
/// </summary>
public interface ISettingsFile
{
    bool Exists();

    string Read();

    /// <summary>Replaces the whole file at once, so a crash halfway never leaves half a file.</summary>
    void Write(string contents);

    /// <summary>
    /// Moves the file next to itself, to a new name that starts with <c>config.json.broken.</c>.
    /// Does nothing when there is no file.
    /// </summary>
    void SetAside();
}
