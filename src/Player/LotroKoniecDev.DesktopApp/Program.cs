using Avalonia;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LotroKoniecDev.DesktopApp;

public sealed class Program
{
    /// <summary>
    /// Debug on purpose, like the CLI's patcher.log: it is the trail players attach to a bug report, and
    /// it never leaves their machine unless they send it (#397).
    /// </summary>
    internal static readonly string LogFilePath = Path.Combine(DesktopAppDependencyInjection.DataFolder, "LotroPoPolsku.log");

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
        // Whoever starts the elevated app picks its working folder, and a path relative to it (the
        // locator's data\ probe, a library Windows looks up there) must not land in the player's
        // folder (ADR-0069 amendment of 2026-11-18).
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        // Before the logger and Avalonia, so a second copy writes nothing and opens no window.
        using RunningCopyClaim runningCopy = RunningCopyClaim.Create(new WindowsRunningMutexOpener());
        if (runningCopy.IsRefused)
        {
            AlreadyRunningMessage.Show();
            return 0;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                LogFilePath,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Every copy runs as administrator, so no copy of ours can make Windows refuse a name: another
        // program holds it (#977). With neither name a second copy is not stopped, and the uninstaller
        // cannot ask to close this one.
        if (!runningCopy.GlobalHeld || !runningCopy.LocalHeld)
        {
            Log.Warning(
                "The app-running mutex could not be made (global: {GlobalHeld}, local: {LocalHeld}); another program holds the name",
                runningCopy.GlobalHeld,
                runningCopy.LocalHeld);
        }

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
