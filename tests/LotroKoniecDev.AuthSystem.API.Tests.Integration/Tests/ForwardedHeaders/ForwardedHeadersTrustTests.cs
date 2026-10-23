using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ForwardedHeaders;

/// <summary>
/// Proves who forwarded headers are trusted from (#399): with <c>ForwardedHeaders:KnownNetworks</c> set,
/// only peers inside those networks, and a malformed network stops the boot. The tests read the scheme from
/// the discovery links. An <see cref="IStartupFilter"/> sets the peer address, because a TestServer
/// connection has none and the middleware skips the check without one.
/// </summary>
[Collection("AuthApi")]
public sealed class ForwardedHeadersTrustTests
{
    private const string TrustedProxyCidr = "10.60.0.0/24";
    /// <summary>
    /// The value the deployed stack actually sets (#506): Caddy's pinned static IP, not its subnet.
    /// Must stay in lockstep with <c>ForwardedHeaders__KnownNetworks__0</c> in compose.hetzner.yaml.
    /// </summary>
    private const string CaddyOnlyCidr = "10.60.0.100/32";

    private readonly AuthSystemApiFactory _appFactory;

    public ForwardedHeadersTrustTests(AuthSystemApiFactory appFactory)
    {
        _appFactory = appFactory;
    }

    [Fact]
    public async Task Discovery_SpoofedForwardedProtoFromPeerOutsideKnownNetworks_KeepsHttpLinks()
    {
        // Arrange: 203.0.113.7 (TEST-NET-3) is outside the trusted proxy subnet.
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(TrustedProxyCidr, peerAddress: "203.0.113.7");
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(factory, request);

        // Assert: the spoofed proto must not flip the scheme.
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("http", $"href for rel='{link.Rel}' must ignore X-Forwarded-Proto from an untrusted peer");
        }
    }

    [Fact]
    public async Task Discovery_ForwardedProtoFromPeerInsideKnownNetworks_BuildsHttpsLinks()
    {
        // Arrange: the peer sits inside the trusted proxy subnet, like the real ingress hop.
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(TrustedProxyCidr, peerAddress: "10.60.0.5");
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(factory, request);

        // Assert: restricting trust must not break the legitimate proxy path.
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("https", $"href for rel='{link.Rel}' must honour the trusted proxy's X-Forwarded-Proto");
        }
    }

    [Fact]
    public async Task Discovery_ForwardedProtoFromCaddysPinnedIp_BuildsHttpsLinks()
    {
        // Arrange: the boundary the boxes run since #506: a single /32, and the peer is that address.
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(CaddyOnlyCidr, peerAddress: "10.60.0.100");
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(factory, request);

        // Assert: narrowing the CIDR to a host address must not break the real ingress hop.
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("https", $"href for rel='{link.Rel}' must honour X-Forwarded-Proto from Caddy's pinned IP");
        }
    }

    [Fact]
    public async Task Discovery_SpoofedForwardedProtoFromNeighbourInCaddysSubnet_KeepsHttpLinks()
    {
        // Arrange: 10.60.0.101 shares Caddy's /24 but is outside its /32 (#506): under a /24 trust, a
        // co-tenant container on the box would be believed.
        using WebApplicationFactory<Program> factory =
            FactoryWithKnownNetworks(CaddyOnlyCidr, peerAddress: "10.60.0.101");
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(factory, request);

        // Assert: the same subnet is not enough; only Caddy's exact address is trusted.
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("http", $"href for rel='{link.Rel}' must ignore a same-subnet neighbour's X-Forwarded-Proto");
        }
    }

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.60.0.0/99")]
    [InlineData("10.60.0.0")]
    public void Startup_MalformedKnownNetworkCidr_FailsFast(string malformedCidr)
    {
        // Arrange
        using WebApplicationFactory<Program> factory = _appFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", malformedCidr));

        // Act & Assert: boot must abort instead of silently widening forwarded-header trust.
        Should.Throw<FormatException>(() => factory.CreateClient());
    }

    [Fact]
    public void Startup_KnownNetworksSetAsScalarWithoutIndex_FailsFast()
    {
        // Arrange: an operator typo: the knob set as a scalar (missing the __0 index) binds to
        // null, which without the guard would silently revert to trust-everyone.
        using WebApplicationFactory<Program> factory = _appFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownNetworks", TrustedProxyCidr));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.CreateClient());
    }

    private WebApplicationFactory<Program> FactoryWithKnownNetworks(string trustedCidr, string peerAddress)
    {
        return _appFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", trustedCidr);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new FakeRemoteIpStartupFilter(IPAddress.Parse(peerAddress))));
        });
    }

    private static HttpRequestMessage HateoasRequest()
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));
        return request;
    }

    private static async Task<DiscoveryResponse> SendDiscoveryAsync(
        WebApplicationFactory<Program> factory,
        HttpRequestMessage request)
    {
        JsonSerializerOptions jsonSerializerOptions =
            factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        using HttpClient httpClient = factory.CreateClient();
        HttpResponseMessage httpResponse = await httpClient.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();
        httpResponse.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);

        return JsonSerializer.Deserialize<DiscoveryResponse>(stringResponse, jsonSerializerOptions)!;
    }

    /// <summary>
    /// Stamps a fake peer address as the first middleware in the pipeline, so the forwarded-headers
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
