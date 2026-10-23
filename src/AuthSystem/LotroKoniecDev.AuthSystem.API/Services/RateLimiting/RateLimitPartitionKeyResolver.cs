using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Names the client a request is metered as (ADR-0054). All frontend calls leave from one container, so
/// the frontend sends the visitor's address with the shared key, and that address counts only when the
/// key matches and the address parses. Everything else is metered on <c>Connection.RemoteIpAddress</c>,
/// which <c>UseForwardedHeaders</c> takes only from Caddy's pinned /32 (#399, #506), so a caller without
/// the key cannot choose its bucket. The keys are compared as SHA-256 digests in constant time, so a
/// wrong guess learns nothing about the key.
/// </summary>
internal sealed class RateLimitPartitionKeyResolver
{
    private const string UnknownClientKey = "unknown";
    private const int IPv6ClientPrefixBits = 64;

    private readonly byte[]? _frontendKeyDigest;

    public RateLimitPartitionKeyResolver(IOptions<FrontendCallerSettings> settings)
    {
        string? configuredKey = settings.Value.Key;
        _frontendKeyDigest = string.IsNullOrWhiteSpace(configuredKey) ? null : Digest(configuredKey);
    }

    public string Resolve(HttpContext httpContext) => KeyFor(ResolveClientAddress(httpContext));

    /// <summary>
    /// The full address behind <see cref="Resolve"/>, which the warning about a refused call names (#854).
    /// </summary>
    public IPAddress? ResolveClientAddress(HttpContext httpContext) =>
        ReadForwardedClientAddress(httpContext.Request.Headers) ?? httpContext.Connection.RemoteIpAddress;

    /// <summary>
    /// One client, one key (#831): an IPv6 client usually owns a whole /64 and can move inside it for
    /// free, so it counts as its /64. <c>::ffff:203.0.113.7</c> counts as plain IPv4.
    /// </summary>
    public static string KeyFor(IPAddress? address)
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
        const int prefixBytes = IPv6ClientPrefixBits / 8;
        Array.Clear(prefix, prefixBytes, prefix.Length - prefixBytes);
        return string.Create(CultureInfo.InvariantCulture, $"{new IPAddress(prefix)}/{IPv6ClientPrefixBits}");
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
