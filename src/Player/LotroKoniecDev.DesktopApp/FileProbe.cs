using LotroKoniecDev.DesktopApp.Presentation;

namespace LotroKoniecDev.DesktopApp;

internal sealed class FileProbe : IFileProbe
{
    public bool FileExists(string path) => File.Exists(path);
}
