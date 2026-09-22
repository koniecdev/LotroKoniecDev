using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.API.Settings;

namespace LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;

/// <summary>
/// Names the client a request is metered as (ADR-0054, #823). The frontend's calls all leave from one
/// container, so it sends the visitor's address along with the environment's shared key: such a call
/// is metered on that address only when the key matches and the address parses. Everything else, a
/// direct caller such as the CLI, a wrong or missing key, a missing, repeated or broken address, is
/// metered on <c>Connection.RemoteIpAddress</c>, which <c>UseForwardedHeaders</c> resolved from Caddy's
/// pinned /32 (#399, #506), so a caller without the key can never choose its bucket. The keys are
/// compared as SHA-256 digests in constant time: a wrong guess learns neither the key's length nor how
/// much of it matched. The auth API has its own copy of this class: the two APIs share the header
/// names, not code.
/// </summary>
internal sealed class RateLimitPartitionKeyResolver
{
    private const string UnknownClientKey = "unknown";

    private readonly byte[]? _frontendKeyDigest;

    public RateLimitPartitionKeyResolver(IOptions<FrontendCallerSettings> settings)
    {
        string? configuredKey = settings.Value.Key;
        _frontendKeyDigest = string.IsNullOrWhiteSpace(configuredKey) ? null : Digest(configuredKey);
    }

    public string Resolve(HttpContext httpContext) =>
        ReadForwardedClientAddress(httpContext.Request.Headers) is { } forwardedClientAddress
            ? forwardedClientAddress.ToString()
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownClientKey;

    private IPAddress? ReadForwardedClientAddress(IHeaderDictionary headers)
    {
        if (_frontendKeyDigest is null
            || headers[FrontendCallerHeaders.Key] is not [{ } presentedKey]
            || !CryptographicOperations.FixedTimeEquals(Digest(presentedKey), _frontendKeyDigest)
            || headers[FrontendCallerHeaders.ClientAddress] is not [{ } forwardedAddress])
        {
            return null;
        }

        return IPAddress.TryParse(forwardedAddress, out IPAddress? clientAddress) ? clientAddress : null;
    }

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
