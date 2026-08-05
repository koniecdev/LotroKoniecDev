using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

/// <summary>
/// Builds the navigation link set for a paged collection: <c>self</c>, <c>first-page</c>,
/// <c>previous-page</c>, <c>next-page</c> and <c>last-page</c>, preserving the active filter query
/// string across every page link.
/// </summary>
internal interface IPaginationLinkFactory
{
    ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        object? additionalRouteValues = null);
}
