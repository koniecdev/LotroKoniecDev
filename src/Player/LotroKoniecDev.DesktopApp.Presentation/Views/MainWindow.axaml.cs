using Avalonia.Controls;
using Avalonia.Interactivity;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Presentation.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The first run and the update check start once the window is on screen. The first run's questions
    /// are panels inside this window, and the disk scan and the update check run in the background, so a
    /// slow or failed one never holds the window back (spec 0016).
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.GameFolder.StartAsync();
            _ = viewModel.AppUpdate.CheckAsync();
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            SettingsWindow settingsWindow = new() { DataContext = viewModel.OpenSettings() };
            _ = settingsWindow.ShowDialog(this);
        }
    }
}
