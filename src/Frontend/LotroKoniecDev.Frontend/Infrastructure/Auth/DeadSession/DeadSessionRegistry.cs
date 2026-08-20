using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class DeadSessionRegistry : IDeadSessionRegistry
{
    internal const string CacheKeyPrefix = "dead-session:";

    // The stored value is the subject itself. What matters is that an entry exists, and a non-empty
    // string reads better than a bool, which the analyzer flags as a redundant literal when cached.
    private const string AbsentMarker = "";

    // A short lifetime. The marker only has to survive from the request that saw the dead token to the
    // very next request, where the cookie validation reads it. A token that fixes itself, for example
    // after a key change the frontend refetches, must not stay marked, so we keep it short.
    private static readonly HybridCacheEntryOptions MarkerEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    // The read runs on every authenticated request, through OnValidatePrincipal, but a marker is rare.
    // Turning cache writes off makes it a pure lookup: on a miss the factory's "not there" value is
    // returned without storing anything, so the cache never fills up with one negative entry per user.
    private static readonly HybridCacheEntryOptions ProbeEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite
    };

    private static readonly string[] DeadSessionTag = ["dead-session"];

    private readonly HybridCache _hybridCache;

    public DeadSessionRegistry(HybridCache hybridCache)
    {
        _hybridCache = hybridCache;
    }

    public async ValueTask MarkDeadAsync(string subject, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        await _hybridCache.SetAsync(
            CacheKeyPrefix + subject,
            subject,
            MarkerEntryOptions,
            DeadSessionTag,
            cancellationToken);
    }

    public async ValueTask<bool> ConsumeAsync(string subject, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        string cacheKey = CacheKeyPrefix + subject;

        string marker = await _hybridCache.GetOrCreateAsync(
            cacheKey,
            static _ => ValueTask.FromResult(AbsentMarker),
            ProbeEntryOptions,
            DeadSessionTag,
            cancellationToken);

        bool isDead = !string.IsNullOrEmpty(marker);
        if (isDead)
        {
            await _hybridCache.RemoveAsync(cacheKey, cancellationToken);
        }

        return isDead;
    }
}
