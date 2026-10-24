namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// <c>Requested</c> counts distinct ids. A row is skipped when its id is unknown, it was already
/// approved, or it can no longer be approved. <c>Approved + Skipped == Requested</c> always holds
/// (#322).
/// </summary>
public sealed record BulkApproveTranslationsResponse(int Requested, int Approved, int Skipped);
