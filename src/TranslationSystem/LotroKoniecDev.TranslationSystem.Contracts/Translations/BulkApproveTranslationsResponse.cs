namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The outcome of a bulk approve (#322): how many distinct rows were requested, how many were
/// actually approved and published, and how many were skipped (unknown id, already approved, or no
/// longer approvable). <c>Approved + Skipped == Requested</c> always holds.
/// </summary>
public sealed record BulkApproveTranslationsResponse(int Requested, int Approved, int Skipped);
