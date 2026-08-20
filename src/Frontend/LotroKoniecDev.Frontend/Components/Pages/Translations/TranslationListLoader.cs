using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// Fetches one page of translations for the list through the typed TMS client, from the page's
/// <see cref="TranslationListQuery"/>. The collection's address comes from the service document's
/// <c>translations</c> rel (#610), which is open to anyone, like the page itself.
/// It stays a thin injectable class, so the search, the status filter and the paging can be unit-tested
/// end to end against a stubbed HTTP handler.
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
    /// Approves several rows at once (#322) with a POST of the selected ids to the collection's
    /// <c>bulk-approve</c> link. <paramref name="bulkApproveHref"/> is the URI the server sent, which it
    /// only sends to a reviewer, and never a path built here.
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
