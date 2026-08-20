namespace LotroKoniecDev.Application.Features.Exporting;

/// <summary>
/// What an export run did, in numbers.
/// </summary>
public sealed record ExportSummaryResponse(
    int TotalTextFiles,
    int TotalFragments,
    string OutputPath);
