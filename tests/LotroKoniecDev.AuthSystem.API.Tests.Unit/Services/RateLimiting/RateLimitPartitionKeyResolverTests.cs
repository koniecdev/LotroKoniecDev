using System.Net;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The client a request is metered as (ADR-0054): the visitor the frontend forwards, but only next to
/// the environment's key, and the connection's own address for everything short of that. Every row
/// here is one step short of a proven visitor; were such a call believed, it would land in a fresh
/// bucket instead of its connection's full one.
/// </summary>
public sealed class RateLimitPartitionKeyResolverTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    private const string ConnectionAddress = "10.60.0.7";
    private const string VisitorAddress = "203.0.113.7";

    [Fact]
    public void Resolve_WithTheKeyAndOneAddress_ReturnsTheVisitorsAddress()
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);
        HttpContext httpContext = ContextFrom(ConnectionAddress, [FrontendKey], [VisitorAddress]);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe(VisitorAddress);
    }

    [Fact]
    public void Resolve_WithTheKeyAndAnIPv6Address_ReturnsItsCanonicalForm()
    {
        // The same string a direct IPv6 caller gets from Connection.RemoteIpAddress.ToString(), so one
        // visitor is one bucket whichever way the call arrives.
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);
        HttpContext httpContext = ContextFrom(ConnectionAddress, [FrontendKey], ["2001:DB8:0:0:0:0:0:1"]);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe("2001:db8::1");
    }

    [Fact]
    public void Resolve_WithNoKeyConfigured_IgnoresBothHeaders()
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(configuredKey: null);
        HttpContext httpContext = ContextFrom(ConnectionAddress, [FrontendKey], [VisitorAddress]);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe(ConnectionAddress);
    }

    public static TheoryData<string[], string[]> CallsShortOfAProvenVisitor => new()
    {
        { [], [VisitorAddress] },
        { [string.Empty], [VisitorAddress] },
        { [new string('w', 40)], [VisitorAddress] },
        { [FrontendKey[..^1]], [VisitorAddress] },
        { [FrontendKey + "k"], [VisitorAddress] },
        { [FrontendKey, FrontendKey], [VisitorAddress] },
        { [FrontendKey], [] },
        { [FrontendKey], [VisitorAddress, "203.0.113.9"] },
        { [FrontendKey], ["not-an-address"] },
        { [FrontendKey], ["198.51.100.39.7"] },
        { [FrontendKey], [VisitorAddress + ", 203.0.113.9"] }
    };

    [Theory]
    [MemberData(nameof(CallsShortOfAProvenVisitor))]
    public void Resolve_ShortOfAProvenVisitor_ReturnsTheConnectionsOwnAddress(string[] keyValues, string[] addressValues)
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);
        HttpContext httpContext = ContextFrom(ConnectionAddress, keyValues, addressValues);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe(ConnectionAddress);
    }

    [Fact]
    public void Resolve_WithNoConnectionAddressAndNoProof_ReturnsTheUnknownBucket()
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);
        HttpContext httpContext = ContextFrom(connectionAddress: null, [], []);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe("unknown");
    }

    private static RateLimitPartitionKeyResolver CreateResolver(string? configuredKey) =>
        new(Microsoft.Extensions.Options.Options.Create(new FrontendCallerSettings { Key = configuredKey }));

    // One Append per value, so two values arrive as a repeated header rather than one comma-joined value.
    private static HttpContext ContextFrom(
        string? connectionAddress,
        IReadOnlyCollection<string> keyValues,
        IReadOnlyCollection<string> addressValues)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = connectionAddress is null ? null : IPAddress.Parse(connectionAddress);

        foreach (string keyValue in keyValues)
        {
            httpContext.Request.Headers.Append(FrontendCallerHeaders.Key, keyValue);
        }

        foreach (string addressValue in addressValues)
        {
            httpContext.Request.Headers.Append(FrontendCallerHeaders.ClientAddress, addressValue);
        }

        return httpContext;
    }
}
