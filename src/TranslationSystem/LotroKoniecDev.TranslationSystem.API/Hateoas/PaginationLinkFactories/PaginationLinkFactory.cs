using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

internal sealed class PaginationLinkFactory : IPaginationLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public PaginationLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public async ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        object? additionalRouteValues = null)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: endpointName,
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: BuildRouteValues(paginationResponse.Page, paginationResponse.PageSize, additionalRouteValues)));

        // A boundary rel that points at the page you are already on is a no-op the pager renders as an
        // enabled button (#545). Unlike previous/next these are ABSOLUTE jumps, so they survive an
        // over-range page (?page=99 of 3) — that is the only way back for a caller who overshot.
        if (paginationResponse.Page != 1)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.FirstPage,
                method: HttpMethods.Get,
                values: BuildRouteValues(1, paginationResponse.PageSize, additionalRouteValues)));
        }

        if (paginationResponse.Page != paginationResponse.TotalPages && paginationResponse.TotalPages > 0)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.LastPage,
                method: HttpMethods.Get,
                values: BuildRouteValues(paginationResponse.TotalPages, paginationResponse.PageSize, additionalRouteValues)));
        }

        if (paginationResponse.Page > 1)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.PreviousPage,
                method: HttpMethods.Get,
                values: BuildRouteValues(paginationResponse.Page - 1, paginationResponse.PageSize, additionalRouteValues)));
        }

        if (paginationResponse.Page < paginationResponse.TotalPages)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.NextPage,
                method: HttpMethods.Get,
                values: BuildRouteValues(paginationResponse.Page + 1, paginationResponse.PageSize, additionalRouteValues)));
        }

        return links;
    }

    private static RouteValueDictionary BuildRouteValues(int page, int pageSize, object? additionalRouteValues)
    {
        RouteValueDictionary values = additionalRouteValues is not null
            ? new RouteValueDictionary(additionalRouteValues)
            : [];
        values["page"] = page;
        values["pageSize"] = pageSize;
        return values;
    }
}
