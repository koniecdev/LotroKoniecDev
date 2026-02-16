namespace LotroKoniecDev.Application.Features.Patch;

/// <summary>
/// Contains summary information about a patch operation.
/// </summary>
public sealed record PatchSummaryResponse(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings);
