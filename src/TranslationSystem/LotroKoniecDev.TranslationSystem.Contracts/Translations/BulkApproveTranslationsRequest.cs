namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// Approves several translation rows in one admin action (#322). It carries the ids selected on the
/// translations list. A row that can no longer be approved by the time the request arrives is
/// skipped, not treated as an error (see <see cref="BulkApproveTranslationsResponse"/>).
/// </summary>
public sealed record BulkApproveTranslationsRequest(IReadOnlyList<Guid> Ids);
