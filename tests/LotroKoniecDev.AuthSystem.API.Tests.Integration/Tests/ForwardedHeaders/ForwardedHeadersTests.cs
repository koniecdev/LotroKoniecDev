using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.ContentNegotiation;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ForwardedHeaders;

/// <summary>
/// The AuthSystem reads the proxy's <c>X-Forwarded-*</c> headers (ADR-0008), so behind a TLS-terminating
/// ingress every absolute URL is https. The tests read the discovery links, built from the request scheme.
/// The <c>iss</c> value comes from <c>OpenIddictSettings.Issuer</c>, not the scheme, so it is not covered.
/// </summary>
public sealed class ForwardedHeadersTests : EndpointsTestBase
{
    public ForwardedHeadersTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task Discovery_WithForwardedProtoHttps_BuildsHttpsAbsoluteLinks()
    {
        // Arrange
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(request);

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("https", $"href for rel='{link.Rel}' must be https behind the proxy");
        }
    }

    [Fact]
    public async Task Discovery_WithoutForwardedProto_BuildsHttpAbsoluteLinks()
    {
        // Arrange: the test server speaks plain http; with no X-Forwarded-Proto the scheme stays
        // http, proving the header (not some unrelated default) is what flips the scheme to https.
        using HttpRequestMessage request = HateoasRequest();

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(request);

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("http");
        }
    }

    [Fact]
    public async Task Discovery_WithForwardedHost_BuildsLinksAgainstForwardedHost()
    {
        // Arrange
        using HttpRequestMessage request = HateoasRequest();
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "auth.lotro-translator.pl");

        // Act
        DiscoveryResponse response = await SendDiscoveryAsync(request);

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("https");
            uri.Host.ShouldBe("auth.lotro-translator.pl");
        }
    }

    private static HttpRequestMessage HateoasRequest()
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));
        return request;
    }

    private async Task<DiscoveryResponse> SendDiscoveryAsync(HttpRequestMessage request)
    {
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();
        httpResponse.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);

        return JsonSerializer.Deserialize<DiscoveryResponse>(stringResponse, ApiClient.JsonOptions)!;
    }
}
