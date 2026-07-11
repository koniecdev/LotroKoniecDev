using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

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

    public async Task<ApiResult<TranslationDiscoveryResponse>> GetTranslationSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // Keyed by auth state because the API tailors its HATEOAS link set per role. A shared key
        // would let the first caller (anon or authed) freeze the wrong link set for everyone for a day.
        string authSuffix = GetAuthSuffix();

        try
        {
            TranslationDiscoveryResponse value = await GetOrCreateTranslationAsync(authSuffix, cancellationToken);
            return ApiResult.Success(value);
        }
        catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
        {
            return ApiResult.Failure<TranslationDiscoveryResponse>(ex.ProblemDetails);
        }
    }

    public async Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // Same poisoning guard idea as the TMS leg, but stricter: the auth root advertises the
        // 'export-account-data' rel only to authenticated callers, so an authenticated key must never
        // cache a response missing it (which would mean the bearer never reached the API) — that
        // would break the whole account section for every signed-in user for a day.
        string authSuffix = GetAuthSuffix();

        try
        {
            AuthDiscoveryResponse value = await GetOrCreateAuthAsync(authSuffix, cancellationToken);
            return ApiResult.Success(value);
        }
        catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
        {
            // Genuine outage (network/5xx) — never reclassified as "session expired".
            return ApiResult.Failure<AuthDiscoveryResponse>(ex.ProblemDetails);
        }
        catch (AuthenticatedLinksDegradedException)
        {
            // The cookie still reads authenticated, but the API answered with the anonymous link set:
            // the bearer token never reached it (expired/invalid/key-rotated). Degrade to the anonymous
            // links AND mark the session dead so the next OnValidatePrincipal signs the cookie out
            // cleanly; the [Authorize] account pages then bounce through login instead of erroring.
            await MarkSessionDeadAsync(cancellationToken);
            AuthDiscoveryResponse anonymous = await GetOrCreateAuthAsync(AnonymousSuffix, cancellationToken);
            return ApiResult.Success(anonymous);
        }
    }

    private async ValueTask<TranslationDiscoveryResponse> GetOrCreateTranslationAsync(
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
                ApiResult<TranslationDiscoveryResponse> result = await client.GetDiscoveryAsync(ct);
                if (result.IsFailure)
                {
                    throw new DiscoveryUnavailableException(result.ProblemDetails);
                }

                return result.Value;
            },
            options: OneDayEntryOptions,
            cancellationToken: cancellationToken);
    }

    private async ValueTask<AuthDiscoveryResponse> GetOrCreateAuthAsync(
        string authSuffix,
        CancellationToken cancellationToken)
    {
        return await _hybridCache.GetOrCreateAsync(
            AuthSystemDiscoveryCacheKeyPrefix + authSuffix,
            (Client: _authSystemClient, RequiresAuthenticatedLinks: authSuffix is not AnonymousSuffix),
            static async (state, ct) =>
            {
                ApiResult<AuthDiscoveryResponse> result = await state.Client.GetDiscoveryAsync(ct);
                if (result.IsFailure)
                {
                    throw new DiscoveryUnavailableException(result.ProblemDetails);
                }

                if (state.RequiresAuthenticatedLinks
                    && !ContainsGetRel(result.Value.Links, AuthRels.ExportAccountData))
                {
                    throw new AuthenticatedLinksDegradedException();
                }

                return result.Value;
            },
            options: OneDayEntryOptions,
            cancellationToken: cancellationToken);
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

/// <summary>
/// Sentinel exception meaning the auth API answered with the anonymous link set under an
/// authenticated cache key — i.e. the bearer token never reached it. Distinct from
/// <see cref="DiscoveryUnavailableException"/> (a genuine outage) so the caller can degrade to a
/// guest link set + dead-session sign-out instead of rendering an error.
/// </summary>
public sealed class AuthenticatedLinksDegradedException : Exception
{
    public AuthenticatedLinksDegradedException()
    {
    }

    public AuthenticatedLinksDegradedException(string message)
        : base(message)
    {
    }

    public AuthenticatedLinksDegradedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
