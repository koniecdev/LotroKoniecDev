namespace LotroKoniecDev.Application.Features.Patching;

/// <param name="TranslationChanged">
/// The translation file differs from the one the last successful patch applied, or no patch was
/// recorded yet, so it was applied in full.
/// </param>
/// <param name="WipedSubFileCount">
/// Text subfiles a game update replaced since our last patch, found while the translation file was
/// unchanged (spec 0012 Tier 0). Zero when nothing was replaced or nothing could be compared.
/// </param>
/// <param name="RepairFailure">Why the repair of those subfiles failed, when it did. Nothing else failed.</param>
public sealed record PatchIfNeededResponse(
    bool TranslationChanged,
    bool TranslationsApplied,
    int AppliedCount,
    int SkippedCount,
    int WipedSubFileCount,
    string? RepairFailure);
