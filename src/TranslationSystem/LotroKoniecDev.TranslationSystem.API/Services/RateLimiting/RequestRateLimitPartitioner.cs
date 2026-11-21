using System.Security.Claims;
using System.Threading.RateLimiting;
using LotroKoniecDev.TranslationSystem.API.Auth;

namespace LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;

/// <summary>
/// Picks the bucket of the general per-minute limit (ADR-0054, amended by #1488). A signed token with a
/// trusted role counts per account, so two reviewers behind one address each get their own room. Every
/// other caller counts per address, as before. The limiter runs after authentication, so only a token
/// whose signature checked out can name an account.
/// </summary>
internal sealed class RequestRateLimitPartitioner
{
    public const int AddressPermitLimit = 100;
    public const int TrustedAccountPermitLimit = 300;

    private const string AccountKeyPrefix = "account:";

    // MapInboundClaims is off, so the OpenIddict claim name arrives unchanged.
    private const string SubjectClaimType = "sub";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RateLimitPartitionKeyResolver _addressKeyResolver;

    public RequestRateLimitPartitioner(RateLimitPartitionKeyResolver addressKeyResolver)
    {
        _addressKeyResolver = addressKeyResolver;
    }

    public RateLimitPartition<string> Partition(HttpContext httpContext)
    {
        (string key, int permitLimit) = Choose(httpContext);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = Window
            });
    }

    public (string Key, int PermitLimit) Choose(HttpContext httpContext)
    {
        ClaimsPrincipal user = httpContext.User;

        if (user.Identity?.IsAuthenticated is true
            && TrustedRoles.AreHeldBy(user)
            && Guid.TryParse(user.FindFirstValue(SubjectClaimType), out Guid accountId))
        {
            return (AccountKeyPrefix + accountId.ToString("D"), TrustedAccountPermitLimit);
        }

        return (_addressKeyResolver.Resolve(httpContext), AddressPermitLimit);
    }
}
