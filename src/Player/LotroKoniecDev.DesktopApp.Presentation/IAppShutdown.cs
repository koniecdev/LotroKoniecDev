namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>Closes the app, so the installer of an update can replace it.</summary>
public interface IAppShutdown
{
    void Shutdown();
}
