using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The args, the old source and the approver come from the detail endpoint. <see cref="Submitter"/> is
/// <c>null</c> while the row is untranslated, and on every row for a caller with no user: no token, a
/// refused (for example expired) token, or a service token (#1543). <see cref="Place"/> is opaque: a client
/// only sends it back as <see cref="ListTranslationsRequest.After"/> (ADR-0077). It is <c>null</c> when the
/// request has a sort.
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
    public string? Place { get; init; }

    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
