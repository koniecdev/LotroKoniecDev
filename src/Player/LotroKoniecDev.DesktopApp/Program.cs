using Avalonia;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

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
        // Debug on purpose, like the CLI's patcher.log: it is the trail players attach to a bug report,
        // and it never leaves their machine unless they send it (#397). The folder is the app's own
        // AppData folder, where #43 keeps the settings.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                DesktopAppDependencyInjection.LogFilePath,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();

            // One scope for the whole process, as in the CLI's TypeResolver. The view models are scoped,
            // like the handlers they inject from #42 on, and ValidateScopes refuses to resolve a scoped
            // service from the root provider.
            using IServiceScope scope = serviceProvider.CreateScope();

            // Startup is raised once Avalonia is set up and just before the lifetime shows its main
            // window, so the splash set here is the first window the player sees.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args,
                lifetime => lifetime.Startup += (_, _) => SplashScreen.Show(
                    lifetime,
                    () => scope.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
                    SplashScreen.Duration));
        }
#pragma warning disable S2139
        catch (Exception ex)
        {
            // A WinExe has no console, so without this line a failed start leaves no trace at all.
            Log.Fatal(ex, "The desktop app terminated unexpectedly");
            throw;
        }
#pragma warning restore S2139
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
