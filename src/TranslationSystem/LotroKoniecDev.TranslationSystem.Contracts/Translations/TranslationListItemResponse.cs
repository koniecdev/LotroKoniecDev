using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// One row of the paginated translation list: the English source, the current Polish (if any), the
/// workflow status and the translator who last submitted Polish (<c>null</c> while untranslated),
/// shown with their display name (ADR-0004), keyed by the <c>(FileId, GossipId)</c> fragment. Full
/// per-row context (args, superseded source, approver) belongs to the get-one detail endpoint.
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
