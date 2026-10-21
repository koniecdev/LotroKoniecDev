using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Presentation.Views;

namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>
/// Opens the splash window first and replaces it with the main window after a short wait.
/// </summary>
public static class SplashScreen
{
    /// <summary>
    /// How long the splash stays before the main window is built. Building it takes milliseconds today,
    /// so a shorter splash would only flicker.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Call it before the lifetime shows its main window, from its <c>Startup</c> event: that is the
    /// step that shows the splash.
    /// </summary>
    public static void Show(
        IClassicDesktopStyleApplicationLifetime desktop,
        Func<MainWindowViewModel> createMainWindowViewModel,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(createMainWindowViewModel);

        SplashWindow splashWindow = new();
        desktop.MainWindow = splashWindow;

        DispatcherTimer.RunOnce(
            () => ReplaceWithMainWindow(desktop, splashWindow, createMainWindowViewModel),
            duration);
    }

    /// <summary>
    /// The main window opens before the splash closes. With the default shutdown mode, closing the last
    /// open window ends the app.
    /// </summary>
    private static void ReplaceWithMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splashWindow,
        Func<MainWindowViewModel> createMainWindowViewModel)
    {
        MainWindow mainWindow = new() { DataContext = createMainWindowViewModel() };
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        splashWindow.Close();
    }
}
