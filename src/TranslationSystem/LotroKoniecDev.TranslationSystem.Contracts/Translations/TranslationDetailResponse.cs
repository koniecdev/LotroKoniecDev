using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// A single translation in full: the English source plus its argument columns (for placeholder
/// validation), the current Polish, the superseded English kept for side-by-side review when a
/// game update invalidated the row, the workflow status, the translator who last submitted Polish
/// (<c>null</c> while still untranslated) and the reviewer who last approved it (<c>null</c> until
/// first approved). Submitter / approver carry their display name (ADR-0004) for the editor. Backs
/// the side-by-side editor.
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
    TranslatorSummaryResponse? Submitter,
    TranslatorSummaryResponse? Approver,
    TranslationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
