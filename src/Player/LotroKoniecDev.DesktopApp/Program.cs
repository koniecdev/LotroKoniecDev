using Avalonia;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.DesktopApp;

public sealed class Program
{
    /// <summary>
    /// Also the Avalonia previewer's entry point, which it finds by this exact name. It must not need
    /// any services.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [STAThread]
    private static int Main(string[] args)
    {
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();

        // One scope for the whole process, as in the CLI's TypeResolver. The view models are scoped, like
        // the handlers they inject from #42 on, and ValidateScopes refuses to resolve a scoped service
        // from the root provider.
        using IServiceScope scope = serviceProvider.CreateScope();

        // Startup is raised once Avalonia is set up and just before the lifetime shows its main window,
        // so the splash set here is the first window the player sees.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            lifetime => lifetime.Startup += (_, _) => SplashScreen.Show(
                lifetime,
                () => scope.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
                SplashScreen.Duration));
    }
}
