using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The translation list's calls to the TMS. The <c>translations</c> rel is open to anyone, like the page.
/// </summary>
internal sealed class TranslationListLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public TranslationListLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<PaginationResponse<TranslationListItemResponse>>> LoadAsync(
        TranslationListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.Translations,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<PaginationResponse<TranslationListItemResponse>>(href.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
            query.ToApiUri(href.Value),
            cancellationToken);
    }

    public Task<ApiResult<BulkApproveTranslationsResponse>> BulkApproveAsync(
        string bulkApproveHref,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bulkApproveHref);
        ArgumentNullException.ThrowIfNull(ids);

        return _client.PostApiResultAsync<BulkApproveTranslationsResponse>(
            bulkApproveHref,
            new BulkApproveTranslationsRequest(ids),
            cancellationToken);
    }
}
