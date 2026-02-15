namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Contains summary information about a patch operation.
/// </summary>
public sealed record PatchSummary(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings);
