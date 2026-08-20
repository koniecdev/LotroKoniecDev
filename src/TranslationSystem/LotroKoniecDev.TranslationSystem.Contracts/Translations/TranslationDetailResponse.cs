using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// One translation with everything the editor needs: the English source and its argument columns
/// (used to check the placeholders), the current Polish, the old English kept for comparison when a
/// game update invalidated the row, the status, the translator who last sent Polish (<c>null</c>
/// while the row is untranslated) and the reviewer who last approved it (<c>null</c> before the first
/// approval). Both people carry their display name (ADR-0004).
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
