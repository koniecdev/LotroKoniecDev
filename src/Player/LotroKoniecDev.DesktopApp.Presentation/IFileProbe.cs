namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>Answers whether a file is on disk. The exe gives the real one, so the UI tests stay pure.</summary>
public interface IFileProbe
{
    bool FileExists(string path);
}
