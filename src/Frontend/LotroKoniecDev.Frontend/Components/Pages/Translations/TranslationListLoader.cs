using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// Fetches a page of translations for the list view through the typed TMS client, given the page's
/// normalized <see cref="TranslationListQuery"/>. The collection's address comes from the service
/// document's <c>translations</c> rel (#610) — anonymous, like the page itself. Kept as a thin
/// injectable seam so the page's search / status-filter / pagination behavior is unit-testable
/// end-to-end over a stubbed HTTP handler (the Frontend has no bUnit for component-level rendering tests).
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

    /// <summary>
    /// Approves several rows at once (#322) by POSTing the selected ids to the collection
    /// <c>bulk-approve</c> link. <paramref name="bulkApproveHref"/> is the server-advertised URI —
    /// present only when the API deems the caller a reviewer — never a FE-constructed path.
    /// </summary>
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
