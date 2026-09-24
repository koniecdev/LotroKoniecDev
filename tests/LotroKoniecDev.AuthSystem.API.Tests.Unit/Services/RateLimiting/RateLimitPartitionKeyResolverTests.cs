using System.Net;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The client a request is metered as (ADR-0054): the visitor the frontend forwards, but only next to
/// the environment's key, and the connection's own address for everything short of that. Each row of
/// the "short of a proven visitor" matrix would, if believed, land in a fresh bucket instead of its
/// connection's full one. The address rows pin what one client is (#831): a whole IPv6 /64, and an
/// IPv4 address however it is written.
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
    public void Resolve_ForAnIPv6Client_ReturnsItsSlash64()
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);
        HttpContext httpContext = ContextFrom("2001:db8:0:1:aaaa:bbbb:cccc:dddd", [], []);

        string partitionKey = resolver.Resolve(httpContext);

        partitionKey.ShouldBe("2001:db8:0:1::/64");
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("2001:db8:0:1::7")]
    [InlineData("::ffff:203.0.113.7")]
    public void Resolve_OneVisitor_GetsTheSameKeyWhicheverWayTheCallArrives(string visitorAddress)
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);

        string directKey = resolver.Resolve(ContextFrom(visitorAddress, [], []));
        string forwardedKey = resolver.Resolve(ContextFrom(ConnectionAddress, [FrontendKey], [visitorAddress]));

        forwardedKey.ShouldBe(directKey);
    }

    /// <summary>
    /// #831: an IPv6 client owns its whole /64, and an IPv4 address written in IPv6 form is still that
    /// IPv4 address. The last row is one address written two ways.
    /// </summary>
    public static TheoryData<string, string, bool> AddressesOfOneClient => BothWays(
    [
        ("2001:db8:0:1::1", "2001:db8:0:1::2"),
        ("2001:db8:0:1::1", "2001:db8:0:1:ffff:ffff:ffff:ffff"),
        ("::ffff:203.0.113.7", "203.0.113.7"),
        ("2001:DB8:0:1:0:0:0:1", "2001:db8:0:1::1")
    ]);

    [Theory]
    [MemberData(nameof(AddressesOfOneClient))]
    public void Resolve_TwoAddressesOfOneClient_ReturnTheSameKey(string firstAddress, string secondAddress, bool throughTheFrontend)
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);

        string firstKey = resolver.Resolve(ContextFor(firstAddress, throughTheFrontend));
        string secondKey = resolver.Resolve(ContextFor(secondAddress, throughTheFrontend));

        secondKey.ShouldBe(firstKey);
    }

    public static TheoryData<string, string, bool> AddressesOfTwoClients => BothWays(
    [
        ("2001:db8:0:1::1", "2001:db8:0:2::1"),
        ("2001:db8:0:1::1", "2001:db9:0:1::1"),
        ("203.0.113.7", "203.0.113.8"),
        ("::ffff:203.0.113.7", "::ffff:203.0.113.8"),
        // An IPv6 address that ends in the same four bytes as an IPv4 one is still another client.
        ("203.0.113.7", "2001:db8::cb00:7107")
    ]);

    [Theory]
    [MemberData(nameof(AddressesOfTwoClients))]
    public void Resolve_TwoClients_ReturnDifferentKeys(string firstAddress, string secondAddress, bool throughTheFrontend)
    {
        RateLimitPartitionKeyResolver resolver = CreateResolver(FrontendKey);

        string firstKey = resolver.Resolve(ContextFor(firstAddress, throughTheFrontend));
        string secondKey = resolver.Resolve(ContextFor(secondAddress, throughTheFrontend));

        secondKey.ShouldNotBe(firstKey);
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

    private static TheoryData<string, string, bool> BothWays(IReadOnlyCollection<(string First, string Second)> addressPairs)
    {
        TheoryData<string, string, bool> data = [];
        foreach ((string first, string second) in addressPairs)
        {
            data.Add(first, second, false);
            data.Add(first, second, true);
        }

        return data;
    }

    private static HttpContext ContextFor(string clientAddress, bool throughTheFrontend) =>
        throughTheFrontend
            ? ContextFrom(ConnectionAddress, [FrontendKey], [clientAddress])
            : ContextFrom(clientAddress, [], []);

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
