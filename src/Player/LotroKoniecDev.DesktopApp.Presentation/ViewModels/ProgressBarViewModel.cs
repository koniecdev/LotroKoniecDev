using System.ComponentModel;
using Avalonia.Threading;
using LotroKoniecDev.Application;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>
/// The main window's progress bar, and the progress port the patcher handlers inject, as
/// <c>ConsoleProgressReporter</c> is the CLI's. One object plays both parts, so the main window and the
/// handlers share it without depending on each other. From #42 on, the main window's view model injects
/// those handlers.
/// </summary>
/// <remarks>
/// The handlers report from whatever thread they run on, and Avalonia only lets the UI thread change a
/// bound property, so every report is posted to the UI thread.
/// </remarks>
public sealed class ProgressBarViewModel : IProgress<OperationProgress>, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Percent
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
        }
    }

    public void Report(OperationProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int percent = ToPercent(value);
        Dispatcher.UIThread.Post(() => Percent = percent);
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
