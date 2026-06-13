using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A single translation in full: the English source plus its argument columns (for placeholder
/// validation), the current Polish, the superseded English kept for side-by-side review when a
/// game update invalidated the row, and the workflow status. Backs the side-by-side editor.
/// </summary>
public sealed record TranslationDetailResponse(
    TranslationId Id,
    int FileId,
    long GossipId,
    string SourceText,
    string? ArgsOrder,
    string? ArgsId,
    string? TranslatedText,
    string? PreviousSourceText,
    TranslationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
