using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Drives the side-by-side editor's three TMS calls through the typed client: load one translation
/// (<c>GET /api/v1/translations/{id}</c>), save the Polish (<c>PUT /api/v1/translations</c>, #100) and
/// approve it (<c>POST /api/v1/translations/{id}/approve</c>, #101). Kept as a thin injectable seam so
/// the editor's load / save / approve behavior is unit-testable end-to-end over a stubbed HTTP handler,
/// and so a bUnit render test can drive the editor through a substituted loader.
/// </summary>
internal sealed class TranslationEditorLoader
{
    private const string TranslationsApiPath = "/api/v1/translations";

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

    public Task<ApiResult<TranslationDetailResponse>> SaveAsync(
        UpsertTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _client.PutApiResultAsync<TranslationDetailResponse>(
            TranslationsApiPath,
            request,
            cancellationToken);
    }

    public Task<ApiResult> ApproveAsync(
        TranslationId id,
        CancellationToken cancellationToken = default)
    {
        return _client.PostApiResultAsync(
            ApproveUri(id),
            EmptyApprovePayload,
            cancellationToken);
    }

    /// <summary>The approve endpoint takes no body; an empty object keeps the typed POST helper happy.</summary>
    private static readonly object EmptyApprovePayload = new();

    private static string DetailUri(TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{TranslationsApiPath}/{id.Value}");

    private static string ApproveUri(TranslationId id) =>
        string.Create(CultureInfo.InvariantCulture, $"{TranslationsApiPath}/{id.Value}/approve");
}
