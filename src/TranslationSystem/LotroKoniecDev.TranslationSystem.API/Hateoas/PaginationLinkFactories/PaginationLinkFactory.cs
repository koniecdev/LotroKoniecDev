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
        Func<int, IEnumerable<KeyValuePair<string, string?>>> queryParametersForPage,
        IEnumerable<KeyValuePair<string, string?>>? selfQueryParameters = null)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: endpointName,
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new RouteValueDictionary(selfQueryParameters ?? queryParametersForPage(paginationResponse.Page))));

        // Not on the page you are on, or the pager draws a dead button (#545). They stay when the page is
        // out of range (?page=99 of 3): the only way back for a caller who went too far.
        if (paginationResponse.Page != 1)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.FirstPage,
                method: HttpMethods.Get,
                values: new RouteValueDictionary(queryParametersForPage(1))));
        }

        if (paginationResponse.Page != paginationResponse.TotalPages && paginationResponse.TotalPages > 0)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.LastPage,
                method: HttpMethods.Get,
                values: new RouteValueDictionary(queryParametersForPage(paginationResponse.TotalPages))));
        }

        if (paginationResponse.Page > 1)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.PreviousPage,
                method: HttpMethods.Get,
                values: new RouteValueDictionary(queryParametersForPage(paginationResponse.Page - 1))));
        }

        if (paginationResponse.Page < paginationResponse.TotalPages)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: endpointName,
                rel: Rels.NextPage,
                method: HttpMethods.Get,
                values: new RouteValueDictionary(queryParametersForPage(paginationResponse.Page + 1))));
        }

        return links;
    }
}
