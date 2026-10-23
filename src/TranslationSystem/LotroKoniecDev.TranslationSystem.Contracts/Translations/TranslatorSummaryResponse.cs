using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A submitter or approver as the editor shows them (ADR-0004).
/// </summary>
public sealed record TranslatorSummaryResponse(TranslatorId Id, string DisplayName);
