using Avalonia.Controls.ApplicationLifetimes;

namespace LotroKoniecDev.DesktopApp.Presentation;

internal sealed class ClassicDesktopAppShutdown : IAppShutdown
{
    public void Shutdown()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
