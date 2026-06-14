namespace LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;

/// <summary>
/// Outcome of merging <c>polish.txt</c> onto the baseline (#28): how many rows were newly approved,
/// how many were already approved with identical content (idempotent re-run), how many matched a
/// soft-removed baseline row and were skipped, and the keys of every line with no baseline row
/// (reported, never inserted — the merge-only rule).
/// </summary>
internal sealed record PolishSeedSummary(
    int Approved,
    int AlreadyApproved,
    int SkippedRemoved,
    IReadOnlyList<string> Unmatched);
