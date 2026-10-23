using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.API.Settings;

namespace LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;

/// <summary>
/// The frontend's calls all come from one container, so it forwards the visitor's address with the
/// environment's key (ADR-0054). That address is used only when the key matches and the address parses;
/// anything else is metered on <c>Connection.RemoteIpAddress</c>, which <c>UseForwardedHeaders</c> takes
/// only from Caddy's pinned /32 (#399), so a caller without the key cannot choose its bucket. Keys compare
/// as SHA-256 digests in constant time, so a guess learns neither length nor prefix. The auth API has its
/// own copy.
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
    /// The full address behind <see cref="Resolve"/>, which the refused-call warning names (#854).
    /// </summary>
    public IPAddress? ResolveClientAddress(HttpContext httpContext) =>
        ReadForwardedClientAddress(httpContext.Request.Headers) ?? httpContext.Connection.RemoteIpAddress;

    /// <summary>
    /// An IPv6 client usually owns a whole /64 and moves inside it for free, so it counts as its /64;
    /// <c>::ffff:a.b.c.d</c> counts as plain IPv4 (#831).
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
