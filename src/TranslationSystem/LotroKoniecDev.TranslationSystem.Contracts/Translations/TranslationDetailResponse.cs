using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// Everything the editor needs. <see cref="PreviousSourceText"/> is the old English kept when a game
/// update invalidated the row. <see cref="Submitter"/> is <c>null</c> while the row is untranslated,
/// <see cref="Approver"/> before the first approval.
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
