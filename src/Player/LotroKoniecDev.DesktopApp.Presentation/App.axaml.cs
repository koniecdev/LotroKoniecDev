using Avalonia.Markup.Xaml;

namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>
/// Holds only the theme. The exe decides which windows open (see <see cref="SplashScreen"/>), so the
/// Avalonia previewer and the headless tests can create this class without any services.
/// </summary>
/// <remarks>
/// The base type is written out because the patcher's <c>LotroKoniecDev.Application</c> namespace
/// hides Avalonia's <c>Application</c> inside this namespace.
/// </remarks>
public sealed partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
