using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Translations;

/// <summary>
/// Fetches a page of translations for the list view through the typed TMS client, given the page's
/// normalized <see cref="TranslationListQuery"/>. Kept as a thin injectable seam so the page's
/// search / status-filter / pagination behavior is unit-testable end-to-end over a stubbed HTTP
/// handler (the Frontend has no bUnit for component-level rendering tests).
/// </summary>
internal sealed class TranslationListLoader
{
    private readonly ITranslationSystemClient _client;

    public TranslationListLoader(ITranslationSystemClient client)
    {
        _client = client;
    }

    public Task<ApiResult<PaginationResponse<TranslationListItemResponse>>> LoadAsync(
        TranslationListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return _client.GetApiResultAsync<PaginationResponse<TranslationListItemResponse>>(
            query.ToApiRelativeUri(),
            cancellationToken);
    }
}
