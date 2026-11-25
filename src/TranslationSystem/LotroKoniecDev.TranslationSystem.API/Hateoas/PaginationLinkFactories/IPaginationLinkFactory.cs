using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

/// <summary>
/// Every page link keeps the filters that are currently applied. The caller's request record writes
/// the query of each page, so this factory holds no option name of its own (#1478). A request that does
/// not name a page, such as the list's <c>after</c> (ADR-0077), passes its own query as
/// <paramref name="selfQueryParameters"/>, so <c>self</c> names the items it holds and the first and last
/// page links always stay.
/// </summary>
internal interface IPaginationLinkFactory
{
    ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        Func<int, IEnumerable<KeyValuePair<string, string?>>> queryParametersForPage,
        IEnumerable<KeyValuePair<string, string?>>? selfQueryParameters = null);
}
