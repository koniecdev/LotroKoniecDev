using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Makes the side-by-side editor's calls to the TMS through the typed client.
/// It starts by loading one row: the collection's address comes from the service document's
/// <c>translations</c> rel (#610) and the row id is added to it, because the editor is opened by its own
/// <c>/editor/{id}</c> route and therefore has an id and not a link.
/// Save and approve stay link-driven (#158, spec 0002): they follow the <c>upsert</c> and
/// <c>approve</c> hrefs the loaded row offers, so the server alone decides who may do what and in which
/// status.
/// It stays a thin injectable class, so load, save and approve can be unit-tested end to end against a
/// stubbed HTTP handler, and a bUnit render test can drive the editor through a substituted loader.
/// </summary>
internal sealed class TranslationEditorLoader
{
    /// <summary>The approve endpoint takes no body, and the typed POST helper needs one, so it gets an empty object.</summary>
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
    /// The <c>upsert</c> entry point from the service document, for the one case where there is no row
    /// to offer it: a resubmit whose row could not be loaded again. Reading it from the server here keeps
    /// even the recovery path free of a path written into the code.
    /// </summary>
    public Task<ApiResult<string>> ResolveCollectionUpsertHrefAsync(CancellationToken cancellationToken = default)
    {
        return _discoveryCache.ResolveTranslationSystemHrefAsync(Rels.Upsert, cancellationToken);
    }

    /// <summary>
    /// Saves the Polish with a PUT to the loaded row's <c>upsert</c> link (#100). The row is named by
    /// <c>(FileId, GossipId)</c> in the body. <paramref name="upsertHref"/> is the URI the server sent
    /// and never a path built here.
    /// </summary>
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

    /// <summary>
    /// Approves the row with a POST to its <c>approve</c> link (#101). <paramref name="approveHref"/> is
    /// the URI the server sent, and it is only there when the API considers the row approvable.
    /// </summary>
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
    /// The one place this frontend still assumes something about the API's URLs, and a deliberate,
    /// limited exception to ADR-0041. The editor is opened by its own <c>/editor/{id}</c> route, so it
    /// has an id and not a link, and the service document has no per-row rel with a placeholder to use
    /// instead. The base still comes from the server; only the id is added.
    /// Remove this the day the API offers a templated <c>translation</c> rel, or the list row's
    /// <c>self</c> link is carried through. It would also break if the <c>translations</c> link ever
    /// gained a query string.
    /// </summary>
    private static string DetailUri(string collectionHref, TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{collectionHref.TrimEnd('/')}/{id.Value}");
}
