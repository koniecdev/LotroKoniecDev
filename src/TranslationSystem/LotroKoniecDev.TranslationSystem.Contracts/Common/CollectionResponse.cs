using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

/// <summary>
/// An unpaged collection envelope: the items plus collection-level hypermedia links (e.g. the
/// collection <c>self</c> and create-type actions). The sibling of <see cref="PaginationResponse{T}"/>
/// for lists that are intentionally unpaged (few rows ever exist, like game versions).
/// </summary>
public sealed record CollectionResponse<T> : ILinksResponse
{
    public required IReadOnlyCollection<T> Items { get; init; }

    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
