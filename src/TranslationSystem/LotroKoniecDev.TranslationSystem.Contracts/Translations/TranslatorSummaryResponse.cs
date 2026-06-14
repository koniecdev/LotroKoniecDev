using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A translator referenced by a translation (submitter / approver), as the editor renders them
/// (ADR-0004): the local <see cref="TranslatorId"/> plus the human-readable display name resolved
/// from the joined translator row.
/// </summary>
public sealed record TranslatorSummaryResponse(TranslatorId Id, string DisplayName);
