using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Common;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;

/// <summary>
/// Every page link keeps the filters that are currently applied. The caller's request record writes
/// the query of each page, so this factory holds no option name of its own (#1478).
/// </summary>
internal interface IPaginationLinkFactory
{
    ValueTask<IReadOnlyList<LinkDto>> CreatePaginationLinksAsync<T>(
        string endpointName,
        PaginationResponse<T> paginationResponse,
        Func<int, IEnumerable<KeyValuePair<string, string?>>> queryParametersForPage);
}
