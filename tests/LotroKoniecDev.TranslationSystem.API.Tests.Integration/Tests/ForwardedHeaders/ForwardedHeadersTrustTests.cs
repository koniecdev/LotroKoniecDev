using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.ForwardedHeaders;

/// <summary>
/// Proves the forwarded-header TRUST boundary (#399) on the TMS host — the twin of the AuthSystem
/// suite's <c>ForwardedHeadersTrustTests</c> (the wiring is deliberately duplicated per host, so
/// each copy is pinned): when <c>ForwardedHeaders:KnownNetworks</c> is configured,
/// <c>X-Forwarded-*</c> is honoured only from peers inside those CIDRs, and a malformed or
/// mis-shaped knob aborts boot instead of silently widening trust. The observable seam is the same
/// as <see cref="ForwardedHeadersTests"/>: the scheme-derived HATEOAS self link. The peer address
/// is injected by a first-in-pipeline middleware (an <see cref="IStartupFilter"/>), because the
/// in-memory TestServer connection carries no RemoteIpAddress — and the middleware skips the
/// known-proxy check entirely for address-less connections.
/// </summary>
[Collection("TranslationApi")]
public sealed class ForwardedHeadersTrustTests : IAsyncLifetime
{
    private const string TrustedProxyCidr = "10.60.0.0/24";
    /// <summary>
    /// The value the deployed stack actually sets (#506): Caddy's pinned static IP, not its subnet.
    /// Must stay in lockstep with <c>ForwardedHeaders__KnownNetworks__0</c> in compose.hetzner.yaml.
    /// </summary>
    private const string CaddyOnlyCidr = "10.60.0.100/32";
    private const string Route = "/api/v1/game-versions";
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public ForwardedHeadersTrustTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetResource_SpoofedForwardedProtoFromPeerOutsideKnownNetworks_KeepsHttpLinks()
    {
        // Arrange — 203.0.113.7 (TEST-NET-3) is outside the trusted proxy subnet.
        GameVersionId id = await SeedAsync("48.0");
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(TrustedProxyCidr, peerAddress: "203.0.113.7");
        using HttpClient client = TranslatorClient(factory);
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert — the spoofed proto must NOT flip the scheme.
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("http", "the self link must ignore X-Forwarded-Proto from an untrusted peer");
    }

    [Fact]
    public async Task GetResource_ForwardedProtoFromPeerInsideKnownNetworks_BuildsHttpsLinks()
    {
        // Arrange — the peer sits inside the trusted proxy subnet, like the real ingress hop.
        GameVersionId id = await SeedAsync("48.1");
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(TrustedProxyCidr, peerAddress: "10.60.0.5");
        using HttpClient client = TranslatorClient(factory);
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert — restricting trust must not break the legitimate proxy path.
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("https", "the self link must honour the trusted proxy's X-Forwarded-Proto");
    }

    [Fact]
    public async Task GetResource_ForwardedProtoFromCaddysPinnedIp_BuildsHttpsLinks()
    {
        // Arrange — the boundary the boxes actually run since #506: a single /32, and the peer IS it.
        GameVersionId id = await SeedAsync("48.2");
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(CaddyOnlyCidr, peerAddress: "10.60.0.100");
        using HttpClient client = TranslatorClient(factory);
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert — narrowing the CIDR to a host address must not break the real ingress hop.
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("https", "the self link must honour X-Forwarded-Proto from Caddy's pinned IP");
    }

    [Fact]
    public async Task GetResource_SpoofedForwardedProtoFromNeighbourInCaddysSubnet_KeepsHttpLinks()
    {
        // Arrange — 10.60.0.101 shares Caddy's /24 but is outside its /32. This is the whole point of
        // #506: a co-tenant container on the box would have been BELIEVED under the old /24 trust.
        GameVersionId id = await SeedAsync("48.3");
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(CaddyOnlyCidr, peerAddress: "10.60.0.101");
        using HttpClient client = TranslatorClient(factory);
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert — same subnet is NOT enough; only Caddy's exact address is trusted.
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("http", "a /32 must not honour X-Forwarded-Proto from a same-subnet neighbour");
    }

    [Fact]
    public void Startup_MalformedKnownNetworkCidr_FailsFast()
    {
        // Arrange — the full malformed-input matrix lives in the AuthSystem twin; one case here
        // pins that THIS host's copy of the wiring fails fast too.
        using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "not-a-cidr"));

        // Act & Assert — boot must abort instead of silently widening forwarded-header trust.
        Should.Throw<FormatException>(() => factory.CreateClient());
    }

    [Fact]
    public void Startup_KnownNetworksSetAsScalarWithoutIndex_FailsFast()
    {
        // Arrange — an operator typo: the knob set as a scalar (missing the __0 index) binds to
        // null, which without the guard would silently revert to trust-everyone.
        using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownNetworks", TrustedProxyCidr));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateClient());
    }

    private WebApplicationFactory<Program> FactoryWithKnownNetworks(string trustedCidr, string peerAddress)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", trustedCidr);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new FakeRemoteIpStartupFilter(IPAddress.Parse(peerAddress))));
        });
    }

    private static HttpClient TranslatorClient(WebApplicationFactory<Program> factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }

    private static HttpRequestMessage HateoasRequest(string url)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));
        return request;
    }

    private static async Task<T> SendHateoasAsync<T>(HttpClient client, HttpRequestMessage request)
        where T : class
    {
        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task<GameVersionId> SeedAsync(string version)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();

        return gameVersion.Id;
    }

    /// <summary>
    /// Stamps a fake peer address as the FIRST middleware in the pipeline, so the forwarded-headers
    /// middleware (also pipeline-first, from Program.cs) sees a connection it can trust-check.
    /// </summary>
    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        private readonly IPAddress _peerAddress;

        public FakeRemoteIpStartupFilter(IPAddress peerAddress)
        {
            _peerAddress = peerAddress;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = _peerAddress;
                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
