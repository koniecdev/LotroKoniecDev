using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;
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
    private const string AccountSuffixPrefix = "user:";
    private const string SubjectClaimType = "sub";
    private const string AccessTokenName = "access_token";

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
        // The TMS root is open to anyone (#608). 'contribution-data-export' needs only a login, so every
        // signed-in caller gets it.
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
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        ClaimsPrincipal? signedInUser =
            httpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;
        string? subject = signedInUser?.FindFirst(SubjectClaimType)?.Value is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
        string? cacheKey = signedInUser is null
            ? cacheKeyPrefix + AnonymousSuffix
            : GetAccountCacheKey(cacheKeyPrefix, subject, signedInUser);

        if (cacheKey is not null)
        {
            TResponse? cached = await _hybridCache.GetOrCreateAsync<TResponse?>(
                cacheKey,
                static _ => ValueTask.FromResult<TResponse?>(null),
                CachedOnlyEntryOptions,
                cancellationToken: cancellationToken);
            if (cached is not null)
            {
                return ApiResult.Success(cached);
            }
        }

        // Never inside a HybridCache factory: a factory can run without the request's context, and this
        // call needs the caller's bearer and address (#825).
        ApiResult<TResponse> live = await fetchLiveAsync(cancellationToken);
        if (live.IsFailure)
        {
            // Never cached, or a short outage would keep the app broken for a day. Never "session expired"
            // either.
            return live;
        }

        if (signedInUser is not null && !ContainsGetRel(live.Value.Links, signedInMarkerRel))
        {
            // Signed in, but the API sent the anonymous links, so it did not accept the token. The next
            // cookie validation signs the user out; this answer still renders the public pages. It is cached
            // under neither key: under the account's key it would hide every signed-in feature for a day.
            await MarkSessionDeadAsync(httpContext, cancellationToken);
            return live;
        }

        if (cacheKey is not null)
        {
            await _hybridCache.SetAsync(cacheKey, live.Value, OneDayEntryOptions, cancellationToken: cancellationToken);
        }

        return live;
    }

    /// <summary>
    /// Marks the token the discovery call carried, not the user (#1061).
    /// </summary>
    private async Task MarkSessionDeadAsync(HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (httpContext is not null && await httpContext.GetTokenAsync(AccessTokenName) is { } accessToken)
        {
            await _deadSessionRegistry.MarkDeadAsync(accessToken, cancellationToken);
        }
    }

    /// <summary>
    /// One entry per account and cookie roles, because the API sends an admin more links (#842). The
    /// cookie keeps the roles from sign-in, so a role change inside one session shows after at most a day
    /// (#896).
    /// </summary>
    /// <returns>
    /// <see langword="null"/> for a session with no subject, whose answer is never cached.
    /// </returns>
    private static string? GetAccountCacheKey(string cacheKeyPrefix, string? subject, ClaimsPrincipal user)
    {
        if (subject is null)
        {
            return null;
        }

        IEnumerable<string> roles = user.Identities
            .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        // The subject's length goes first, so no subject and role set can spell another account's key.
        return $"{cacheKeyPrefix}{AccountSuffixPrefix}{subject.Length}:{subject}:{string.Join(',', roles)}";
    }

    private static bool ContainsGetRel(IEnumerable<LinkDto> links, string rel) =>
        links.Any(link => link.Method == HttpMethods.Get && link.Rel == rel);
}
