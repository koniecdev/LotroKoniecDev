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
    /// The first run starts once the window is on screen: its questions are panels inside this window,
    /// and the disk scan runs in the background, so the window never waits for it.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.GameFolder.StartAsync();
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
