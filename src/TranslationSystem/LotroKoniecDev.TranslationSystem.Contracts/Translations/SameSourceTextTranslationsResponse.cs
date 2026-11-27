using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The other live rows whose English text is exactly the same as one row's (#1347). <see cref="Items"/>
/// holds at most a page of them, rows with Polish first; <see cref="TotalCount"/> counts them all.
/// <see cref="HasConflictingTranslations"/> covers the whole live group, the asked row included.
/// </summary>
/// <param name="EmptyIdenticalCount">
/// The other live rows with no Polish whose args match too, which the <c>fill-identical</c> link would
/// fill (#1587).
/// </param>
public sealed record SameSourceTextTranslationsResponse(
    IReadOnlyCollection<SameSourceTextItemResponse> Items,
    int TotalCount,
    bool HasConflictingTranslations,
    int EmptyIdenticalCount = 0) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
