namespace LotroKoniecDev.Application.Features.Exporting;

/// <summary>
/// Contains summary information about an export operation.
/// </summary>
public sealed record ExportSummaryResponse(
    int TotalTextFiles,
    int TotalFragments,
    string OutputPath);
