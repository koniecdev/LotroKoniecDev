using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

/// <summary>
/// A list without paging: the items plus the links that belong to the collection itself, such as
/// <c>self</c> and the create actions. It is the counterpart of <see cref="PaginationResponse{T}"/>
/// for lists that never need paging, like game versions, where only a few rows ever exist.
/// </summary>
public sealed record CollectionResponse<T> : ILinksResponse
{
    public required IReadOnlyCollection<T> Items { get; init; }

    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
