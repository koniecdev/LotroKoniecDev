using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// The editor's calls to the TMS. Save and approve follow the <c>upsert</c> and <c>approve</c> hrefs the
/// loaded row offers, so the server alone decides who may do what (spec 0002). A thin class, so a bUnit
/// test can drive the editor through a substituted loader.
/// </summary>
internal sealed class TranslationEditorLoader
{
    /// <summary>The approve endpoint takes no body, but the typed POST helper needs one.</summary>
    private static readonly object EmptyApprovePayload = new();

    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public TranslationEditorLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<TranslationDetailResponse>> LoadAsync(
        TranslationId id,
        CancellationToken cancellationToken = default)
    {
        ApiResult<string> collectionHref = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.Translations,
            cancellationToken);
        if (collectionHref.IsFailure)
        {
            return ApiResult.Failure<TranslationDetailResponse>(collectionHref.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<TranslationDetailResponse>(
            DetailUri(collectionHref.Value, id),
            cancellationToken);
    }

    /// <summary>
    /// For the one case with no row to offer the link: a resubmit whose row could not be loaded again.
    /// </summary>
    public Task<ApiResult<string>> ResolveCollectionUpsertHrefAsync(CancellationToken cancellationToken = default)
    {
        return _discoveryCache.ResolveTranslationSystemHrefAsync(Rels.Upsert, cancellationToken);
    }

    /// <summary>The row is named by <c>(FileId, GossipId)</c> in the body.</summary>
    public Task<ApiResult<TranslationDetailResponse>> SaveAsync(
        string upsertHref,
        UpsertTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upsertHref);
        ArgumentNullException.ThrowIfNull(request);

        return _client.PutApiResultAsync<TranslationDetailResponse>(
            upsertHref,
            request,
            cancellationToken);
    }

    public Task<ApiResult> ApproveAsync(
        string approveHref,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approveHref);

        return _client.PostApiResultAsync(
            approveHref,
            EmptyApprovePayload,
            cancellationToken);
    }

    /// <summary>
    /// The one place this frontend assumes something about the API's URLs, a limited exception to
    /// ADR-0041. The <c>/editor/{id}</c> route hands over an id, not a link, and the service document has
    /// no templated per-row rel. It would break if the <c>translations</c> link gained a query string.
    /// </summary>
    private static string DetailUri(string collectionHref, TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{collectionHref.TrimEnd('/')}/{id.Value}");
}
