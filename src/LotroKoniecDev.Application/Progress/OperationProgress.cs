namespace LotroKoniecDev.Application.Progress;

/// <summary>
/// Structured progress report for long-running operations (Export, Patch, etc.).
/// Used with <see cref="IProgress{T}"/> to decouple progress reporting from presentation.
/// Each presentation layer (CLI, Web, WPF) provides its own <see cref="IProgress{OperationProgress}"/>
/// implementation — the Application layer only reports progress, never decides how to display it.
/// </summary>
public sealed record OperationProgress
{
    /// <summary>
    /// Name of the running operation (e.g., "Export", "Patch").
    /// </summary>
    public required string OperationName { get; init; }

    /// <summary>
    /// Number of items processed so far.
    /// </summary>
    public required int Current { get; init; }

    /// <summary>
    /// Total number of items to process.
    /// </summary>
    public required int Total { get; init; }

    /// <summary>
    /// Optional detail message (e.g., "Processing file 620756992...").
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Completion percentage (0.0–100.0). Returns 0 if <see cref="Total"/> is zero.
    /// </summary>
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
}
