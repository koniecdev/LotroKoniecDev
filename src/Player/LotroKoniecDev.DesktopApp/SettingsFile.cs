using System.Text;
using LotroKoniecDev.DesktopApp.Presentation.Settings;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// <c>config.json</c> in the app's AppData folder. A save writes a new file next to it and moves it over
/// the old one, so a crash never leaves half a file. The temporary name is random, so nothing can be
/// placed at it in advance.
/// </summary>
internal sealed class SettingsFile : ISettingsFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;

    public bool Exists() => File.Exists(_path);

    public string Read() => File.ReadAllText(_path, Utf8WithoutBom);

    public void Write(string contents)
    {
        string folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        string temporaryPath = Path.Combine(folder, $"{Path.GetFileName(_path)}.{Path.GetRandomFileName()}");

        try
        {
            // CreateNew never opens a file that is already there under that name.
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream, Utf8WithoutBom))
            {
                writer.Write(contents);
            }

            // File.Replace needs an existing target, which the first save does not have.
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public SettingsFile(string path)
    {
        _path = path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The save already failed, and that is the error the caller reports.
        }
    }
}
