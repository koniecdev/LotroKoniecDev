using LotroKoniecDev.Application.Progress;

namespace LotroKoniecDev;

/// <summary>
/// CLI implementation of <see cref="IProgress{OperationProgress}"/>.
/// Renders progress as a single overwriting line using carriage return.
/// Web (Blazor) and Desktop (WPF) will provide their own implementations
/// (e.g., SignalR push, WPF progress bar binding).
/// </summary>
internal sealed class ConsoleProgressReporter : IProgress<OperationProgress>
{
    public void Report(OperationProgress value)
    {
        string message = value.StatusMessage
            ?? $"{value.OperationName}... {value.Current:N0}/{value.Total:N0} ({value.Percentage:F0}%)";

        Console.Write($"\r{message}".PadRight(60));
    }
}
