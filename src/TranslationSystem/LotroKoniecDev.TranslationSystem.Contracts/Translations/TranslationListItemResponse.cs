using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// One row of the paginated translation list: the English source, the current Polish (if any) and
/// the workflow status, keyed by the <c>(FileId, GossipId)</c> fragment. Full per-row context
/// (args, superseded source) belongs to the get-one detail endpoint.
/// </summary>
public sealed record TranslationListItemResponse(
    TranslationId Id,
    int FileId,
    long GossipId,
    string SourceText,
    string? TranslatedText,
    TranslationStatus Status,
    DateTimeOffset UpdatedAt);
