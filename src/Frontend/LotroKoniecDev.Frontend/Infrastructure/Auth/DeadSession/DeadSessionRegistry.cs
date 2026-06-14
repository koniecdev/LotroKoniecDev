using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class DeadSessionRegistry : IDeadSessionRegistry
{
    internal const string CacheKeyPrefix = "dead-session:";

    // Stored value is the marked subject itself: presence is the signal, and a non-empty string is a
    // truthier marker than a bool (which the analyzer flags as a redundant literal when cached).
    private const string AbsentMarker = "";

    // Short TTL: the marker only has to survive the gap between the request that observed the dead
    // token and the immediately-following request whose principal validation consumes it. A
    // self-healing token (a key roll the FE refetches) must not stay flagged, so we keep it brief.
    private static readonly HybridCacheEntryOptions MarkerEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    // The consume path runs on every authenticated request (via OnValidatePrincipal) but a marker is
    // rare. Disabling cache writes makes the read a pure probe: on a miss the factory's absent value
    // is returned without persisting an entry, so we never pollute the cache with per-user negatives.
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
