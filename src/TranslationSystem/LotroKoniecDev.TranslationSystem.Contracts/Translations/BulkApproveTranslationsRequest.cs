namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A row that can no longer be approved when the request arrives is skipped, not an error (#322, see
/// <see cref="BulkApproveTranslationsResponse"/>).
/// </summary>
public sealed record BulkApproveTranslationsRequest(IReadOnlyList<Guid> Ids);
