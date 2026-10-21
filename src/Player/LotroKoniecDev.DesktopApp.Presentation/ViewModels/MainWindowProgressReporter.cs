using Avalonia.Threading;
using LotroKoniecDev.Application;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The desktop app's answer to the progress port the patcher handlers inject, as
/// <c>ConsoleProgressReporter</c> is the CLI's. It moves the main window's progress bar.
/// </summary>
/// <remarks>
/// The handlers report from whatever thread they run on, and Avalonia only lets the UI thread change
/// a bound property, so every update is posted to the UI thread.
/// </remarks>
internal sealed class MainWindowProgressReporter : IProgress<OperationProgress>
{
    private readonly MainWindowViewModel _viewModel;

    public void Report(OperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int percent = ToPercent(value);
        Dispatcher.UIThread.Post(() => _viewModel.ProgressPercent = percent);
    }

    public MainWindowProgressReporter(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    /// <summary>
    /// A report with nothing left to do counts as done, the same rule as
    /// <see cref="OperationProgress.IsCompleted"/>. A total of zero or less never reaches the division.
    /// </summary>
    private static int ToPercent(OperationProgress progress)
    {
        if (progress.IsCompleted)
        {
            return 100;
        }

        if (progress.Total <= 0)
        {
            return 0;
        }

        long percent = (long)progress.Current * 100 / progress.Total;
        return (int)Math.Clamp(percent, 0, 100);
    }
}
