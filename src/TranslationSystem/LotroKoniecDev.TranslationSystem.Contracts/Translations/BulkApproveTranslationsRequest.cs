namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// Approves several translation rows in one admin action (#322): the ids of the rows selected on the
/// translations list. Best-effort — rows that are no longer approvable when the request lands are
/// skipped, not rejected (see <see cref="BulkApproveTranslationsResponse"/>).
/// </summary>
public sealed record BulkApproveTranslationsRequest(IReadOnlyList<Guid> Ids);
