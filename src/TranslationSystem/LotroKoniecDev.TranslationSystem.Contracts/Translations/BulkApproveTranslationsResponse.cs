namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// What a bulk approve did (#322): how many different rows were asked for, how many were really
/// approved and published, and how many were skipped because the id is unknown, the row was already
/// approved or it can no longer be approved. <c>Approved + Skipped == Requested</c> always holds.
/// </summary>
public sealed record BulkApproveTranslationsResponse(int Requested, int Approved, int Skipped);
