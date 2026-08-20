using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.ContentNegotiation;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ForwardedHeaders;

/// <summary>
/// Proves the AuthSystem reads the reverse proxy's <c>X-Forwarded-*</c> headers (Program.cs
/// <c>UseForwardedHeaders</c>, ADR-0008, M6-02). Behind an ingress that terminates TLS, the request
/// scheme reads <c>https</c>, so every absolute URL built from the scheme is https. That is the riskiest
/// part of this ticket.
/// What we look at is the public discovery document's links, which <c>LinkGenerator</c> builds from
/// <c>HttpContext.Request.Scheme</c>, exactly the value the forwarded headers change.
/// The <c>iss</c> value in OpenIddict tokens and discovery is deliberately not covered here: it comes
/// from <c>OpenIddictSettings.Issuer</c>, does not depend on the request scheme, and so cannot be
/// affected by these headers.
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
        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        return JsonSerializer.Deserialize<DiscoveryResponse>(stringResponse, ApiClient.JsonOptions)!;
    }
}
