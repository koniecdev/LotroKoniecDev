using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// One row of <see cref="SameSourceTextTranslationsResponse"/>. The English is left out, because it is the
/// asked row's text. <see cref="Submitter"/> is <c>null</c> while the row is untranslated.
/// </summary>
public sealed record SameSourceTextTranslationResponse(
    TranslationId Id,
    int FileId,
    long GossipId,
    string? TranslatedText,
    TranslationStatus Status,
    TranslatorSummaryResponse? Submitter);
