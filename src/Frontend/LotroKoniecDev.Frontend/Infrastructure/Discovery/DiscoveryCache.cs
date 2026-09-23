using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.Extensions.Caching.Hybrid;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationRels = LotroKoniecDev.TranslationSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Infrastructure.Discovery;

internal sealed class DiscoveryCache : IDiscoveryCache
{
    internal const string TranslationSystemDiscoveryCacheKeyPrefix = "discovery:translation-system:";
    internal const string AuthSystemDiscoveryCacheKeyPrefix = "discovery:auth-system:";

    private const string AnonymousSuffix = "anon";
    private const string UserSuffix = "user";
    private const string SubjectClaimType = "sub";

    private static readonly HybridCacheEntryOptions OneDayEntryOptions = new()
    {
        Expiration = TimeSpan.FromDays(1),
        LocalCacheExpiration = TimeSpan.FromDays(1)
    };

    private static readonly HybridCacheEntryOptions CachedOnlyEntryOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData
    };

    private readonly HybridCache _hybridCache;
    private readonly ITranslationSystemClient _translationSystemClient;
    private readonly IAuthSystemClient _authSystemClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDeadSessionRegistry _deadSessionRegistry;

    public DiscoveryCache(
        HybridCache hybridCache,
        ITranslationSystemClient translationSystemClient,
        IAuthSystemClient authSystemClient,
        IHttpContextAccessor httpContextAccessor,
        IDeadSessionRegistry deadSessionRegistry)
    {
        _hybridCache = hybridCache;
        _translationSystemClient = translationSystemClient;
        _authSystemClient = authSystemClient;
        _httpContextAccessor = httpContextAccessor;
        _deadSessionRegistry = deadSessionRegistry;
    }

    public Task<ApiResult<TranslationDiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // The TMS root is open to anyone (#608) and sends different links per caller. The marker is
        // 'contribution-data-export': its endpoint needs nothing but a login, so every logged-in caller
        // gets it, just like 'export-account-data' on the auth side.
        return GetDiscoveryAsync(
            TranslationSystemDiscoveryCacheKeyPrefix,
            TranslationRels.ContributionDataExport,
            _translationSystemClient.GetDiscoveryAsync,
            cancellationToken);
    }

    public Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // The auth root offers 'export-account-data' only to logged-in callers.
        return GetDiscoveryAsync(
            AuthSystemDiscoveryCacheKeyPrefix,
            AuthRels.ExportAccountData,
            _authSystemClient.GetDiscoveryAsync,
            cancellationToken);
    }

    private async Task<ApiResult<TResponse>> GetDiscoveryAsync<TResponse>(
        string cacheKeyPrefix,
        string signedInMarkerRel,
        Func<CancellationToken, Task<ApiResult<TResponse>>> fetchLiveAsync,
        CancellationToken cancellationToken)
        where TResponse : class, ILinksResponse
    {
        // The key includes whether the caller is logged in, because the API sends a different set of
        // links per role. With one shared key, whoever called first would fix the wrong set for everyone
        // for a day.
        string authSuffix = GetAuthSuffix();
        string cacheKey = cacheKeyPrefix + authSuffix;

        TResponse? cached = await _hybridCache.GetOrCreateAsync<TResponse?>(
            cacheKey,
            static _ => ValueTask.FromResult<TResponse?>(null),
            CachedOnlyEntryOptions,
            cancellationToken: cancellationToken);
        if (cached is not null)
        {
            return ApiResult.Success(cached);
        }

        // Never inside a HybridCache factory: a factory can run without the request's context, and this
        // call needs the caller's bearer and address (#825).
        ApiResult<TResponse> live = await fetchLiveAsync(cancellationToken);
        if (live.IsFailure)
        {
            // A real outage, a network error or a 5xx. It is never cached, because a short outage would
            // then keep the app broken for a day, and it is never turned into "session expired".
            return live;
        }

        if (authSuffix is UserSuffix && !ContainsGetRel(live.Value.Links, signedInMarkerRel))
        {
            // The cookie says the user is logged in, but the API sent the anonymous set of links, so the
            // token never reached it: it expired, is invalid, or its key was rotated. Mark the session
            // dead, so the next cookie validation signs the user out cleanly, and serve this set, so the
            // public pages still render on the way out. It is cached under neither key: under the
            // logged-in key it would take every signed-in feature away from everyone for a day, and an
            // answer to a call that carried a bearer is not what an anonymous call gets.
            await MarkSessionDeadAsync(cancellationToken);
            return live;
        }

        await _hybridCache.SetAsync(cacheKey, live.Value, OneDayEntryOptions, cancellationToken: cancellationToken);
        return live;
    }

    private async Task MarkSessionDeadAsync(CancellationToken cancellationToken)
    {
        string? subject = _httpContextAccessor.HttpContext?.User.FindFirst(SubjectClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            await _deadSessionRegistry.MarkDeadAsync(subject, cancellationToken);
        }
    }

    private string GetAuthSuffix()
    {
        HttpContext? context = _httpContextAccessor.HttpContext;
        return context?.User.Identity?.IsAuthenticated is true
            ? UserSuffix
            : AnonymousSuffix;
    }

    private static bool ContainsGetRel(IEnumerable<LinkDto> links, string rel) =>
        links.Any(link => link.Method == HttpMethods.Get && link.Rel == rel);
}
