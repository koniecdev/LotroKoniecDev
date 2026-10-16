using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class DeadSessionRegistry : IDeadSessionRegistry
{
    internal const string CacheKeyPrefix = "dead-session:";

    /// <summary>
    /// What matters is that an entry exists. A non-empty string reads better than a bool, which the analyzer
    /// flags as a redundant literal when cached.
    /// </summary>
    private const string DeadMarker = "dead";
    private const string AbsentMarker = "";

    // A short lifetime. The marker only has to survive from the request that saw the dead token to the
    // very next request, where the cookie validation reads it. A token that fixes itself, for example
    // after a key change the API picks up, must not stay marked, so we keep it short.
    private static readonly HybridCacheEntryOptions MarkerEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    // The read runs on every authenticated request, through OnValidatePrincipal, but a marker is rare.
    // Turning cache writes off makes it a pure lookup: on a miss the factory's "not there" value is
    // returned without storing anything, so the cache never fills up with one negative entry per session.
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
    /// The key is the access token (see <see cref="IDeadSessionRegistry"/>), hashed for two reasons. The cache
    /// skips a key longer than 256 characters with only a log line, and a token is longer than that. And a
    /// bearer token should not sit in the cache as a key.
    /// </summary>
    private static string CacheKeyFor(string accessToken) =>
        CacheKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
}
