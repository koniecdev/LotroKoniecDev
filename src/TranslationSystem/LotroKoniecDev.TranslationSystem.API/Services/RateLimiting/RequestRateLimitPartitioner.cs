using System.Security.Claims;
using System.Threading.RateLimiting;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;

/// <summary>
/// Picks the bucket of the general per-minute limit (ADR-0054, amended by #1488). A signed token with
/// Reviewer counts per account, so two reviewers behind one address each get their own room. The admin
/// has no limit, so the owner can run scripted work. Every other caller counts per address. The limiter
/// runs after authentication, so only a token whose signature checked out can name an account.
/// </summary>
internal sealed class RequestRateLimitPartitioner
{
    public const int AddressPermitLimit = 100;
    public const int ReviewerPermitLimit = 300;

    private const string AccountKeyPrefix = "account:";

    // MapInboundClaims is off, so the OpenIddict claim name arrives unchanged.
    private const string SubjectClaimType = "sub";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RateLimitPartitionKeyResolver _addressKeyResolver;

    public RateLimitPartition<string> Partition(HttpContext httpContext)
    {
        (string key, int? permitLimit) = Choose(httpContext);

        if (permitLimit is not int limit)
        {
            return RateLimitPartition.GetNoLimiter(key);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = Window
            });
    }

    /// <summary>A null permit limit means no limit at all.</summary>
    public (string Key, int? PermitLimit) Choose(HttpContext httpContext)
    {
        ClaimsPrincipal user = httpContext.User;

        if (user.Identity?.IsAuthenticated is true
            && Guid.TryParse(user.FindFirstValue(SubjectClaimType), out Guid accountId))
        {
            string accountKey = AccountKeyPrefix + accountId.ToString("D");

            if (user.IsInRole(AuthConstants.Roles.Admin))
            {
                return (accountKey, null);
            }

            if (user.IsInRole(AuthConstants.Roles.Reviewer))
            {
                return (accountKey, ReviewerPermitLimit);
            }
        }

        return (_addressKeyResolver.Resolve(httpContext), AddressPermitLimit);
    }

    public RequestRateLimitPartitioner(RateLimitPartitionKeyResolver addressKeyResolver)
    {
        _addressKeyResolver = addressKeyResolver;
    }
}
