using Avalonia.Controls;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Presentation.Views;

public sealed partial class SettingsWindow : Window
{
    private SettingsViewModel? _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>The view model lives as long as the app, so the window lets go of its event when it closes.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is SettingsViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.CloseRequested -= OnCloseRequested;
        _viewModel = null;

        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
