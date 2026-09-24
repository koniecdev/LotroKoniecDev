using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.API.Settings;

namespace LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;

/// <summary>
/// Names the client a request is metered as (ADR-0054, #823). The frontend's calls all leave from one
/// container, so it sends the visitor's address along with the environment's shared key: such a call
/// is metered on that address only when the key matches and the address parses. Everything else is
/// metered on <c>Connection.RemoteIpAddress</c>: a direct caller such as the CLI, a wrong or missing
/// key, and a missing, repeated or broken address. <c>UseForwardedHeaders</c> resolved that address
/// from Caddy's pinned /32 (#399, #506), so a caller without the key can never choose its bucket.
/// The keys are compared as SHA-256 digests in constant time: a wrong guess learns neither the key's
/// length nor how much of it matched. The auth API has its own copy of this class: the two APIs share
/// the header names, not code.
/// </summary>
internal sealed class RateLimitPartitionKeyResolver
{
    private const string UnknownClientKey = "unknown";
    private const int Slash64PrefixLength = 8;

    private readonly byte[]? _frontendKeyDigest;

    public RateLimitPartitionKeyResolver(IOptions<FrontendCallerSettings> settings)
    {
        string? configuredKey = settings.Value.Key;
        _frontendKeyDigest = string.IsNullOrWhiteSpace(configuredKey) ? null : Digest(configuredKey);
    }

    public string Resolve(HttpContext httpContext) =>
        KeyFor(ReadForwardedClientAddress(httpContext.Request.Headers) ?? httpContext.Connection.RemoteIpAddress);

    /// <summary>
    /// One client, one key (#831). An IPv6 client usually owns a whole /64 and can move to a new address
    /// inside it for free, so an IPv6 address counts as its /64. An IPv4 address written in IPv6 form
    /// (<c>::ffff:203.0.113.7</c>) counts as the IPv4 address it is.
    /// </summary>
    private static string KeyFor(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClientKey;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily is not AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        byte[] prefix = address.GetAddressBytes();
        Array.Clear(prefix, Slash64PrefixLength, prefix.Length - Slash64PrefixLength);
        return $"{new IPAddress(prefix)}/64";
    }

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
