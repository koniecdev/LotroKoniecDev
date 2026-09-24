using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// ADR-0054 (#823): every call the frontend makes reaches the TMS API from its one container, so the
/// API meters it on the visitor's address the frontend forwards, but only next to the environment's
/// key. The limiter is forced on for a derived host, as on the auth API; one test boots a Staging host
/// instead, to prove the environment alone still turns it on there. In Testing
/// <c>UseForwardedHeaders</c> trusts every peer, so <c>X-Forwarded-For</c> plays the connection address
/// Caddy resolves: <c>10.60.0.x</c> is the frontend container, RFC 5737 addresses are visitors. Every
/// test creates its own host, so its buckets are its own. The full matrix of calls short of a proven
/// visitor is a unit test on the resolver; the rows here prove the wiring.
/// </summary>
[Collection("TranslationApi")]
public sealed class FrontendCallerKeyTests : IAsyncLifetime
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    /// <summary>Mirrors the fixed-by-ip policy every TMS endpoint sits on: 100 requests per minute per client.</summary>
    private const int Bucket = 100;

    private const string FrontendAddress = "10.60.0.7";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string DiscoveryPath = "/";
    private const string GameVersionsPath = "/api/v1/game-versions";

    private readonly TranslationSystemApiFactory _factory;

    public FrontendCallerKeyTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoTranslatorsBehindTheFrontend_ShouldEachGetTheirOwnBucket()
    {
        // Arrange: both translators browse through the same frontend container, and one of them spends
        // a whole bucket on page loads, the way #823 describes.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        string heavyToken = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);
        string bystanderToken = TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator);
        Caller heavyVisitor = Caller.ThroughTheFrontend("203.0.113.10");
        Caller bystanderVisitor = Caller.ThroughTheFrontend("203.0.113.11");

        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage pageLoad = await GetAsync(client, GameVersionsPath, heavyVisitor, heavyToken);
            pageLoad.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage bucketSpent = await GetAsync(client, GameVersionsPath, heavyVisitor, heavyToken);
        bucketSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act: the bystander loads a page from the same container
        using HttpResponseMessage bystanderPageLoad = await GetAsync(client, GameVersionsPath, bystanderVisitor, bystanderToken);

        // Assert: the bucket that filled is the first visitor's, not the container's
        bystanderPageLoad.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProvenVisitorCalls_ShouldNotSpendTheFrontendContainersOwnBucket()
    {
        // Arrange: a visitor spends a whole bucket through the frontend
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustBucketAsync(client, Caller.ThroughTheFrontend("203.0.113.20"));

        // Act: a call from the container itself, with neither header
        using HttpResponseMessage containersOwnCall = await GetAsync(client, DiscoveryPath, Caller.Direct(FrontendAddress));

        // Assert
        containersOwnCall.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AVisitor_ShouldHaveOneBucket_WhicheverWayTheCallArrives()
    {
        // Arrange: the visitor spends the bucket directly, the way the CLI calls the API
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustBucketAsync(client, Caller.Direct("203.0.113.30"));

        // Act
        using HttpResponseMessage throughTheFrontend = await GetAsync(
            client, DiscoveryPath, Caller.ThroughTheFrontend("203.0.113.30"));
        using HttpResponseMessage anotherVisitor = await GetAsync(
            client, DiscoveryPath, Caller.ThroughTheFrontend("203.0.113.31"));

        // Assert: one visitor, one bucket; the container's key does not buy a second one
        throughTheFrontend.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        anotherVisitor.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string, string[], string[]> CallsShortOfAProvenVisitor => new()
    {
        { "10.60.0.31", [], ["198.51.100.31"] },
        { "10.60.0.32", [new string('w', 40)], ["198.51.100.32"] },
        { "10.60.0.33", [FrontendKey], ["not-an-address"] },
        { "10.60.0.34", [FrontendKey], ["198.51.100.34", "198.51.100.35"] }
    };

    [Theory]
    [MemberData(nameof(CallsShortOfAProvenVisitor))]
    public async Task CallShortOfAProvenVisitor_ShouldBeMeteredOnItsConnection(
        string connectionAddress,
        string[] keyValues,
        string[] addressValues)
    {
        // Arrange: the connection's own bucket is full
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustBucketAsync(client, Caller.Direct(connectionAddress));

        // Act: the same connection names a fresh visitor, without the proof that makes it count
        using HttpResponseMessage response = await GetAsync(
            client, DiscoveryPath, new Caller(connectionAddress, keyValues, addressValues));

        // Assert: it was metered on the full bucket, not on the address it named
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task DeployedEnvironment_ShouldEnforceTheLimitWithoutTheTestSwitch()
    {
        // Arrange: a Staging host, where only the environment turns the limiter on. A gate that
        // needed RateLimiting:ForceEnable would leave staging and prod with no TMS limit at all.
        using WebApplicationFactory<Program> stagingHost = CreateStagingHost();
        using HttpClient client = stagingHost.CreateClient();
        Caller caller = Caller.Direct("203.0.113.60");

        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, DiscoveryPath, caller);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage overTheLimit = await GetAsync(client, DiscoveryPath, caller);

        // Assert
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private sealed record Caller(string ConnectionAddress, IReadOnlyCollection<string> KeyValues, IReadOnlyCollection<string> AddressValues)
    {
        public static Caller Direct(string address) => new(address, [], []);

        public static Caller ThroughTheFrontend(string visitorAddress) => new(FrontendAddress, [FrontendKey], [visitorAddress]);
    }

    // One Add per value, so two values arrive as a repeated header rather than one comma-joined value.
    // X-Forwarded-Proto is what Caddy sends; it keeps a Staging host's HTTPS redirect out of the way.
    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        Caller caller,
        string? accessToken = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(ForwardedForHeader, caller.ConnectionAddress);
        request.Headers.Add(ForwardedProtoHeader, "https");

        foreach (string keyValue in caller.KeyValues)
        {
            request.Headers.Add(FrontendCallerHeaders.Key, keyValue);
        }

        foreach (string addressValue in caller.AddressValues)
        {
            request.Headers.Add(FrontendCallerHeaders.ClientAddress, addressValue);
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request);
    }

    // Spends a whole bucket on the anonymous discovery root. Not an assertion: a bucket that did not
    // fill is a broken precondition for the test that called this, so it throws.
    private static async Task ExhaustBucketAsync(HttpClient client, Caller caller)
    {
        for (int i = 0; i < Bucket; i++)
        {
            using HttpResponseMessage response = await GetAsync(client, DiscoveryPath, caller);
        }

        using HttpResponseMessage probe = await GetAsync(client, DiscoveryPath, caller);
        if (probe.StatusCode != HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                $"The bucket did not fill after {Bucket} requests from {caller.ConnectionAddress} (the probe answered {probe.StatusCode}).");
        }
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" },
                    { "FrontendCaller:Key", FrontendKey }
                });
            });
        });
    }

    // Staging carries every setting a deployed host needs to boot, and not the test switch.
    private WebApplicationFactory<Program> CreateStagingHost()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Cors:AllowedOrigins:0", "https://app.lotro.test" },
                    { "FrontendCaller:Key", FrontendKey },
                    { "HealthCheck:Key", new string('h', 32) }
                });
            });
        });
    }
}
