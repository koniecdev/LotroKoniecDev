using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;

namespace LotroKoniecDev.DesktopApp.Presentation.Views;

public sealed partial class MainWindow : Window
{
    private bool _isOpened;

    /// <summary>Stands in for the screen's work area in tests: the headless platform has one fixed screen.</summary>
    internal Func<PixelRect?>? WorkAreaSource { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        PositionChanged += OnPositionChanged;
        ScalingChanged += OnScalingChanged;
    }

    /// <summary>
    /// The first run and the update check start once the window is on screen. The first run's questions
    /// are panels inside this window, and the disk scan and the update check run in the background, so a
    /// slow or failed one never holds the window back (spec 0016).
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _isOpened = true;
        LimitHeightToWorkArea();

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.GameFolder.StartAsync();
            _ = viewModel.AppUpdate.CheckAsync();
        }
    }

    /// <summary>
    /// The window grows after it is placed (a found game, an update offer), and Windows keeps its top
    /// where it was, so every resize moves it back into the work area (#1463). The move is posted: on
    /// Windows a resize arrives inside the layout pass, and a restore reports its size before its state.
    /// </summary>
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        Dispatcher.UIThread.Post(FitIntoWorkArea);
    }

    /// <summary>
    /// The player's close waits for a Patch or Play run: the end of the app disposes the game file's
    /// handler, which closes its native handles under a write still in progress. No code of ours closes
    /// this window, and the update's shutdown is not a window close, so it is never refused.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.CloseReason == WindowCloseReason.WindowClosing
            && DataContext is MainWindowViewModel viewModel
            && viewModel.PatchAndPlay.RefuseClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            SettingsWindow settingsWindow = new() { DataContext = viewModel.OpenSettings() };
            _ = settingsWindow.ShowDialog(this);
        }
    }

    /// <summary>
    /// A move alone only sets the limit, because the player put the window there. If the new limit makes the
    /// window shorter, the resize moves it back into the work area.
    /// </summary>
    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => LimitHeightToWorkArea();

    private void OnScalingChanged(object? sender, EventArgs e) => LimitHeightToWorkArea();

    private void LimitHeightToWorkArea()
    {
        if (!_isOpened || !IsVisible || CurrentWorkArea() is not { } workArea)
        {
            return;
        }

        if (ScreenFit.MaxClientHeight(workArea, DesktopScaling, TitleBarHeight()) is { } maxHeight)
        {
            MaxHeight = maxHeight;
        }
    }

    private void FitIntoWorkArea()
    {
        if (!_isOpened || !IsVisible || WindowState == WindowState.Minimized || CurrentWorkArea() is not { } workArea)
        {
            return;
        }

        int frameHeight = (int)Math.Ceiling((FrameSize ?? ClientSize).Height * DesktopScaling);
        int top = ScreenFit.FitTop(workArea, Position.Y, frameHeight);
        if (top != Position.Y)
        {
            Position = new PixelPoint(Position.X, top);
        }
    }

    private PixelRect? CurrentWorkArea() =>
        WorkAreaSource is not null ? WorkAreaSource() : Screens.ScreenFromWindow(this)?.WorkingArea;

    /// <summary>The height limit covers only the client area, so the title bar comes off the work area.</summary>
    private double TitleBarHeight() => (FrameSize ?? ClientSize).Height - ClientSize.Height;
}
