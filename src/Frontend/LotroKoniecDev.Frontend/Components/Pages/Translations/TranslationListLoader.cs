using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// The translation list's calls to the TMS. The <c>translations</c> rel is open to anyone, like the page;
/// the save and the bulk approve need the rels the server gives only to the callers who may use them.
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

    /// <summary>
    /// A row's save from the list (#1493). The list is not loaded on that post, so the href comes from the
    /// service document. It is the same collection PUT each row's <c>upsert</c> link names, and the
    /// editor uses it the same way when its row cannot be loaded. The server alone decides who may save.
    /// </summary>
    public async Task<ApiResult<TranslationDetailResponse>> SaveAsync(
        UpsertTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.Upsert,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<TranslationDetailResponse>(href.ProblemDetails!);
        }

        return await _client.PutApiResultAsync<TranslationDetailResponse>(
            href.Value,
            request,
            cancellationToken);
    }
}
