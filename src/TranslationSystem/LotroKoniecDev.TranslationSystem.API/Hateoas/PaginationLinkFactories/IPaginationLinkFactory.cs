using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

/// <summary>
/// Every page link keeps the filters that are currently applied.
/// </summary>
internal interface IPaginationLinkFactory
{
    ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        object? additionalRouteValues = null);
}
