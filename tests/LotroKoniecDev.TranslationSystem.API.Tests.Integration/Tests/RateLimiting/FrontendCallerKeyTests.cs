using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// Pins the wiring of ADR-0054: a frontend call is metered on the forwarded visitor only next to the
/// key. The full matrix is the resolver's unit test. Two tests boot a deployed host (Production, plus
/// Staging for any other name, #1187): the environment alone turns the limiter on, and the host refuses
/// to start without the key. In Testing <c>X-Forwarded-For</c> plays the address Caddy resolves:
/// <c>10.60.0.x</c> is the frontend container, RFC 5737 addresses are visitors. Each test has its own
/// host, so its own buckets.
/// </summary>
[Collection("TranslationApi")]
public sealed class FrontendCallerKeyTests : IAsyncLifetime
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    /// <summary>Must match the fixed-by-ip policy in Program.cs.</summary>
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
        // Arrange: one of two translators behind the frontend spends a whole bucket (#823).
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
        // Arrange
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustBucketAsync(client, Caller.ThroughTheFrontend("203.0.113.20"));

        // Act: the container's own call, with neither header
        using HttpResponseMessage containersOwnCall = await GetAsync(client, DiscoveryPath, Caller.Direct(FrontendAddress));

        // Assert
        containersOwnCall.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AVisitor_ShouldHaveOneBucket_WhicheverWayTheCallArrives()
    {
        // Arrange: spent directly, the way the CLI calls
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();
        await ExhaustBucketAsync(client, Caller.Direct("203.0.113.30"));

        // Act
        using HttpResponseMessage throughTheFrontend = await GetAsync(
            client, DiscoveryPath, Caller.ThroughTheFrontend("203.0.113.30"));
        using HttpResponseMessage anotherVisitor = await GetAsync(
            client, DiscoveryPath, Caller.ThroughTheFrontend("203.0.113.31"));

        // Assert: the container's key does not buy a second bucket
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

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DeployedEnvironment_ShouldEnforceTheLimitWithoutTheTestSwitch(string environmentName)
    {
        // Arrange: a gate that needed RateLimiting:ForceEnable would leave the boxes with no limit.
        using WebApplicationFactory<Program> deployedHost = CreateDeployedHost(environmentName, FrontendKey);
        using HttpClient client = deployedHost.CreateClient();
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

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Boot_InADeployedEnvironmentWithoutTheKey_ShouldFailNamingTheKey(string environmentName)
    {
        // Arrange: the test above boots this host with the key, so the key is the one reason this boot
        // can fail (#915).
        using WebApplicationFactory<Program> deployedHost = CreateDeployedHost(environmentName, frontendKey: null);

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => deployedHost.CreateClient());

        // Assert
        exception.Message.ShouldContain("FrontendCaller:Key", Case.Sensitive);
    }

    private sealed record Caller(string ConnectionAddress, IReadOnlyCollection<string> KeyValues, IReadOnlyCollection<string> AddressValues)
    {
        public static Caller Direct(string address) => new(address, [], []);

        public static Caller ThroughTheFrontend(string visitorAddress) => new(FrontendAddress, [FrontendKey], [visitorAddress]);
    }

    /// <summary>
    /// One Add per value, so two values arrive as a repeated header. X-Forwarded-Proto, as Caddy sends
    /// it, keeps a deployed host's HTTPS redirect out of the way.
    /// </summary>
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

    /// <summary>
    /// Throws, not asserts: a bucket that did not fill is a broken precondition.
    /// </summary>
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

    /// <summary>
    /// Everything a deployed host needs except the key, so the key is the one thing that can stop the
    /// boot. No test switch: only the environment may turn the limiter on.
    /// </summary>
    private WebApplicationFactory<Program> CreateDeployedHost(string environmentName, string? frontendKey)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Cors:AllowedOrigins:0", "https://app.lotro.test" },
                    { "FrontendCaller:Key", frontendKey },
                    { "HealthCheck:Key", new string('h', 32) }
                });
            });
        });
    }
}
