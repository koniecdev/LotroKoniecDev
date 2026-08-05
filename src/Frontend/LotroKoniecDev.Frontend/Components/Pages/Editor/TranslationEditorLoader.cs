using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Drives the side-by-side editor's TMS calls through the typed client. Loading one row is the entry
/// point: the collection's address comes from the service document's <c>translations</c> rel (#610) and
/// the row id is appended, because the editor is reached by its own <c>/editor/{id}</c> route and so
/// holds an id rather than a link. Save and approve stay link-driven (#158 / spec 0002) — they follow
/// the <c>upsert</c> / <c>approve</c> hypermedia <c>href</c> the loaded row advertises, so the server
/// alone decides the role + Draft/NeedsReview affordance. Kept as a thin injectable seam so the
/// editor's load / save / approve behavior is unit-testable end-to-end over a stubbed HTTP handler, and
/// so a bUnit render test can drive the editor through a substituted loader.
/// </summary>
internal sealed class TranslationEditorLoader
{
    /// <summary>The approve endpoint takes no body; an empty object keeps the typed POST helper happy.</summary>
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
    /// The collection-level <c>upsert</c> entry point from the service document, for the one case that
    /// has no row to advertise it: a resubmit whose row could not be reloaded. Resolving it here keeps
    /// the recovery path server-addressed rather than falling back to a compiled-in path.
    /// </summary>
    public Task<ApiResult<string>> ResolveCollectionUpsertHrefAsync(CancellationToken cancellationToken = default)
    {
        return _discoveryCache.ResolveTranslationSystemHrefAsync(Rels.Upsert, cancellationToken);
    }

    /// <summary>
    /// Saves the Polish by PUTting to the loaded row's <c>upsert</c> link (#100, keyed by
    /// <c>(FileId, GossipId)</c> in the body). <paramref name="upsertHref"/> is the server-advertised
    /// URI — never a FE-constructed path.
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
    /// Approves the row by POSTing to its <c>approve</c> link (#101). <paramref name="approveHref"/> is
    /// the server-advertised URI — present only when the API itself deems the row approvable.
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
    /// The one place this frontend still assumes something about the API's URI space, and a deliberate,
    /// bounded exception to ADR-0041: the editor is reached by its own <c>/editor/{id}</c> route, so it
    /// holds an id rather than a link, and the service document carries no templated per-row rel to
    /// resolve instead. The base is still server-supplied — only the id is appended. Retire this the day
    /// the API advertises a templated <c>translation</c> rel (or the list row's <c>self</c> is carried
    /// through); it would also break if the <c>translations</c> link ever gained a query string.
    /// </summary>
    private static string DetailUri(string collectionHref, TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{collectionHref.TrimEnd('/')}/{id.Value}");
}
