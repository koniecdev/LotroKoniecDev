using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The args, the old source and the approver come from the detail endpoint. <see cref="Submitter"/> is
/// <c>null</c> while the row is untranslated.
/// </summary>
public sealed record TranslationListItemResponse(
    TranslationId Id,
    int FileId,
    long GossipId,
    string SourceText,
    string? TranslatedText,
    TranslationStatus Status,
    TranslatorSummaryResponse? Submitter,
    DateTimeOffset UpdatedAt) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
