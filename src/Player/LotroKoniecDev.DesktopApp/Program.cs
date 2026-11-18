using Avalonia;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LotroKoniecDev.DesktopApp;

public sealed class Program
{
    /// <summary>
    /// Held while the app runs, under this name and with the <c>Global\</c> prefix, as Inno Setup
    /// advises for more than one session. The uninstaller checks both and asks the player to close the
    /// app first, because it cannot remove a running exe (installer/LotroPoPolsku.iss, spec 0016). The
    /// update does not check them: Setup closes the app itself.
    /// </summary>
    private const string RunningMutexName = "LotroPoPolsku_Running";

    /// <summary>
    /// Debug on purpose, like the CLI's patcher.log: it is the trail players attach to a bug report, and
    /// it never leaves their machine unless they send it (#397). The folder is the app's own AppData
    /// folder, where #43 keeps the settings.
    /// </summary>
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LotroPoPolsku",
        "LotroPoPolsku.log");

    /// <summary>
    /// Also the Avalonia previewer's entry point, which it finds by this exact name. It must not need
    /// any services.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// A mutex another account made first, or a different kind of object under the same name, makes
    /// Windows refuse this one. The app must start anyway: without the mutex only the uninstaller's
    /// question is lost.
    /// </summary>
    internal static Mutex? TryCreateMutex(string name)
    {
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        using Mutex? runningMutex = TryCreateMutex(RunningMutexName);
        using Mutex? globalRunningMutex = TryCreateMutex(@"Global\" + RunningMutexName);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                LogFilePath,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Made before the logger, so a refusal is written down here. A refusal is what another
        // account's copy of the app causes, so it is Information (#977).
        if (runningMutex is null || globalRunningMutex is null)
        {
            Log.Information(
                "The app-running mutex could not be made (local: {LocalMade}, global: {GlobalMade}); the uninstaller cannot ask to close this copy",
                runningMutex is not null,
                globalRunningMutex is not null);
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
