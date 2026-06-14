using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal sealed class DiscoveryCache : IDiscoveryCache
{
    internal const string TranslationSystemDiscoveryCacheKeyPrefix = "discovery:translation-system:";

    private const string AnonymousSuffix = "anon";
    private const string UserSuffix = "user";

    private static readonly HybridCacheEntryOptions OneDayEntryOptions = new()
    {
        Expiration = TimeSpan.FromDays(1),
        LocalCacheExpiration = TimeSpan.FromDays(1)
    };

    private readonly HybridCache _hybridCache;
    private readonly ITranslationSystemClient _translationSystemClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DiscoveryCache(
        HybridCache hybridCache,
        ITranslationSystemClient translationSystemClient,
        IHttpContextAccessor httpContextAccessor)
    {
        _hybridCache = hybridCache;
        _translationSystemClient = translationSystemClient;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<ApiResult<DiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // Keyed by auth state because the API tailors its HATEOAS link set per role. A shared key
        // would let the first caller (anon or authed) freeze the wrong link set for everyone for a day.
        string authSuffix = GetAuthSuffix();

        try
        {
            DiscoveryResponse value = await GetOrCreateAsync(authSuffix, cancellationToken);
            return ApiResult.Success(value);
        }
        catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
        {
            return ApiResult.Failure<DiscoveryResponse>(ex.ProblemDetails);
        }
    }

    private async ValueTask<DiscoveryResponse> GetOrCreateAsync(
        string authSuffix,
        CancellationToken cancellationToken)
    {
        // Only successful, correctly-shaped payloads are cached. A ProblemDetails failure must never be
        // persisted under the 1-day TTL (it would keep the app broken for 24h after a transient outage):
        // the factory throws so HybridCache discards the entry and the next request retries the live
        // endpoint.
        return await _hybridCache.GetOrCreateAsync(
            TranslationSystemDiscoveryCacheKeyPrefix + authSuffix,
            _translationSystemClient,
            static async (client, ct) =>
            {
                ApiResult<DiscoveryResponse> result = await client.GetDiscoveryAsync(ct);
                if (result.IsFailure)
                {
                    throw new DiscoveryUnavailableException(result.ProblemDetails);
                }

                return result.Value;
            },
            options: OneDayEntryOptions,
            cancellationToken: cancellationToken);
    }

    private string GetAuthSuffix()
    {
        HttpContext? context = _httpContextAccessor.HttpContext;
        return context?.User.Identity?.IsAuthenticated is true
            ? UserSuffix
            : AnonymousSuffix;
    }
}

/// <summary>
/// Sentinel exception used to bubble an API failure out of the <c>HybridCache</c> factory without
/// having it persisted as a cache entry. The public 3-constructor shape appeases CA1032.
/// </summary>
public sealed class DiscoveryUnavailableException : Exception
{
    public DiscoveryUnavailableException()
    {
    }

    public DiscoveryUnavailableException(string message)
        : base(message)
    {
    }

    public DiscoveryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DiscoveryUnavailableException(ProblemDetails? problemDetails)
    {
        ProblemDetails = problemDetails;
    }

    public ProblemDetails? ProblemDetails { get; }
}
