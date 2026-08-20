using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// One row of the paged translation list, keyed by the <c>(FileId, GossipId)</c> fragment: the
/// English source, the current Polish if there is any, the status and the translator who last sent
/// Polish (<c>null</c> while the row is untranslated), shown with their display name (ADR-0004).
/// The rest of the row, such as the args, the old source and the approver, comes from the detail
/// endpoint.
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
