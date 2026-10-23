using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

/// <summary>
/// The unpaged counterpart of <see cref="PaginationResponse{T}"/>, for lists with only a few rows,
/// like game versions.
/// </summary>
public sealed record CollectionResponse<T> : ILinksResponse
{
    public required IReadOnlyCollection<T> Items { get; init; }

    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
