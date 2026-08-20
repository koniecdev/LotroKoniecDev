using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

/// <summary>
/// Builds the navigation links for a paged list: <c>self</c>, <c>first-page</c>,
/// <c>previous-page</c>, <c>next-page</c> and <c>last-page</c>. Every page link keeps the filters that
/// are currently applied.
/// </summary>
internal interface IPaginationLinkFactory
{
    ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        object? additionalRouteValues = null);
}
