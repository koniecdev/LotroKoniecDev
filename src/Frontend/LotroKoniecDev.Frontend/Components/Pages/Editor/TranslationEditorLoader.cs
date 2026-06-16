using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Drives the side-by-side editor's three TMS calls through the typed client: load one translation
/// (<c>GET /api/v1/translations/{id}</c>) is the FE-built entry point, while save and approve are
/// link-driven (#158 / spec 0002) — they follow the <c>upsert</c> / <c>approve</c> hypermedia
/// <c>href</c> the loaded row advertises rather than a hardcoded path, so the server alone decides the
/// role + Draft/NeedsReview affordance. Kept as a thin injectable seam so the editor's load / save /
/// approve behavior is unit-testable end-to-end over a stubbed HTTP handler, and so a bUnit render test
/// can drive the editor through a substituted loader.
/// </summary>
internal sealed class TranslationEditorLoader
{
    private const string TranslationsApiPath = "/api/v1/translations";

    /// <summary>The approve endpoint takes no body; an empty object keeps the typed POST helper happy.</summary>
    private static readonly object EmptyApprovePayload = new();

    private readonly ITranslationSystemClient _client;

    public TranslationEditorLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    public Task<ApiResult<TranslationDetailResponse>> LoadAsync(
        TranslationId id,
        CancellationToken cancellationToken = default)
    {
        return _client.GetApiResultAsync<TranslationDetailResponse>(
            DetailUri(id),
            cancellationToken);
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

    private static string DetailUri(TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{TranslationsApiPath}/{id.Value}");
}
