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
        // The key includes whether the caller is logged in, because the API sends a different set of
        // links per role. With one shared key, whoever called first would fix the wrong set for everyone
        // for a day.
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
        catch (AuthenticatedLinksDegradedException)
        {
            // The same handling as on the auth side: the cookie says the user is logged in, but the API
            // sent the anonymous set of links, so the token never reached it. Mark the session dead, so
            // the next cookie validation signs the user out cleanly, and return the anonymous links, so
            // the public pages still render instead of failing on the way out.
            await MarkSessionDeadAsync(cancellationToken);

            try
            {
                TranslationDiscoveryResponse anonymous =
                    await GetOrCreateTranslationAsync(AnonymousSuffix, cancellationToken);
                return ApiResult.Success(anonymous);
            }
            catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
            {
                // The API failed between the two calls. Return the error as a value instead of letting
                // the exception escape and turn the page into a 500.
                return ApiResult.Failure<TranslationDiscoveryResponse>(ex.ProblemDetails);
            }
        }
    }

    public async Task<ApiResult<AuthDiscoveryResponse>> GetAuthSystemDiscoveryAsync(
        CancellationToken cancellationToken = default)
    {
        // The same guard as on the TMS side. The auth root offers the 'export-account-data' rel only to
        // logged-in callers, so a response without it must never be cached under a logged-in key: that
        // would mean the token never reached the API, and it would break the whole account section for
        // every signed-in user for a day.
        string authSuffix = GetAuthSuffix();

        try
        {
            AuthDiscoveryResponse value = await GetOrCreateAuthAsync(authSuffix, cancellationToken);
            return ApiResult.Success(value);
        }
        catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
        {
            // A real outage, a network error or a 5xx. It is never turned into "session expired".
            return ApiResult.Failure<AuthDiscoveryResponse>(ex.ProblemDetails);
        }
        catch (AuthenticatedLinksDegradedException)
        {
            // The cookie still says the user is logged in, but the API sent the anonymous set of links,
            // so the token never reached it: it expired, is invalid, or its key was rotated. Fall back
            // to the anonymous links and mark the session dead, so the next cookie validation signs the
            // user out cleanly. The [Authorize] account pages then send them to login instead of
            // failing.
            await MarkSessionDeadAsync(cancellationToken);

            try
            {
                AuthDiscoveryResponse anonymous = await GetOrCreateAuthAsync(AnonymousSuffix, cancellationToken);
                return ApiResult.Success(anonymous);
            }
            catch (DiscoveryUnavailableException ex) when (ex.ProblemDetails is not null)
            {
                // The API failed between the two calls. Return the error as a value instead of letting
                // the exception escape and turn the page into a 500.
                return ApiResult.Failure<AuthDiscoveryResponse>(ex.ProblemDetails);
            }
        }
    }

    private async ValueTask<TranslationDiscoveryResponse> GetOrCreateTranslationAsync(
        string authSuffix,
        CancellationToken cancellationToken)
    {
        // Only successful responses of the right shape are cached. A ProblemDetails failure must never
        // be stored for a day, because a short outage would then keep the app broken for 24 hours. The
        // factory throws instead, so HybridCache drops the entry and the next request calls the live
        // endpoint again. The same rule applies to an incomplete set of links; see the check below.
        return await _hybridCache.GetOrCreateAsync(
            TranslationSystemDiscoveryCacheKeyPrefix + authSuffix,
            (Client: _translationSystemClient, RequiresAuthenticatedLinks: authSuffix is not AnonymousSuffix),
            static async (state, ct) =>
            {
                ApiResult<TranslationDiscoveryResponse> result = await state.Client.GetDiscoveryAsync(ct);
                if (result.IsFailure)
                {
                    throw new DiscoveryUnavailableException(result.ProblemDetails);
                }

                // The TMS root is open to anyone (#608) and sends different links per caller, so a
                // logged-in key that comes back with only the anonymous set means the token never
                // reached the API. Caching that would take the dashboard, the editor and the admin pages
                // away from every signed-in user for a day.
                // 'contribution-data-export' is the marker we look for: its endpoint needs nothing but a
                // login, so every logged-in caller gets it, just like 'export-account-data' on the auth
                // side.
                if (state.RequiresAuthenticatedLinks
                    && !ContainsGetRel(result.Value.Links, TranslationRels.ContributionDataExport))
                {
                    throw new AuthenticatedLinksDegradedException();
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
/// Carries an API failure out of the <c>HybridCache</c> factory, so it is not stored as a cache entry.
/// The three public constructors are there to satisfy CA1032.
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
/// Means the auth API sent the anonymous set of links under a logged-in cache key, so the token never
/// reached it. It is a separate type from <see cref="DiscoveryUnavailableException"/>, which is a real
/// outage, so the caller can fall back to the guest links and sign the dead session out instead of
/// showing an error.
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
