using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// The <c>translation-stats</c> rel is offered only to a translator, so a session without that role gets a
/// clear failure instead of a guessed call (#610).
/// </summary>
internal sealed class DashboardStatsLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly ITranslationSystemClient _client;

    public DashboardStatsLoader(IDiscoveryCache discoveryCache, ITranslationSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    public async Task<ApiResult<TranslationStatsResponse>> LoadAsync(CancellationToken cancellationToken = default)
    {
        ApiResult<string> href = await _discoveryCache.ResolveTranslationSystemHrefAsync(
            Rels.TranslationStats,
            cancellationToken);
        if (href.IsFailure)
        {
            return ApiResult.Failure<TranslationStatsResponse>(href.ProblemDetails!);
        }

        return await _client.GetApiResultAsync<TranslationStatsResponse>(href.Value, cancellationToken);
    }
}
