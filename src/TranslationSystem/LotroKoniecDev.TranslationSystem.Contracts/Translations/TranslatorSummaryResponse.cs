using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A translator a translation points at, as submitter or approver, in the form the editor shows
/// (ADR-0004): the local <see cref="TranslatorId"/> and the display name read from the joined
/// translator row.
/// </summary>
public sealed record TranslatorSummaryResponse(TranslatorId Id, string DisplayName);
