using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class DeadSessionRegistry : IDeadSessionRegistry
{
    internal const string CacheKeyPrefix = "dead-session:";

    /// <summary>
    /// Only the entry's existence matters. Not a bool, which the analyzer flags as a redundant literal.
    /// </summary>
    private const string DeadMarker = "dead";
    private const string AbsentMarker = "";

    // The marker only has to reach the next request. A token that fixes itself, such as after a key
    // change, must not stay marked.
    private static readonly HybridCacheEntryOptions MarkerEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    // The read runs on every authenticated request, but a marker is rare. With writes off, a miss stores
    // nothing, so the cache never fills with one negative entry per session.
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

    public async ValueTask MarkDeadAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        await _hybridCache.SetAsync(
            CacheKeyFor(accessToken),
            DeadMarker,
            MarkerEntryOptions,
            DeadSessionTag,
            cancellationToken);
    }

    public async ValueTask<bool> ConsumeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        string cacheKey = CacheKeyFor(accessToken);

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

    /// <summary>
    /// Hashed: the cache skips a key over 256 characters with only a log line, and a bearer token should not
    /// sit in the cache as a key.
    /// </summary>
    private static string CacheKeyFor(string accessToken) =>
        CacheKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
}
