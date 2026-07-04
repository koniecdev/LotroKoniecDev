using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.ContentNegotiation;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Hateoas;

public sealed class DiscoveryHateoasTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";

    public DiscoveryHateoasTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task Discovery_ShouldReturnAnonymousLinks_WhenNotAuthenticated()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();

        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        DiscoveryResponse response = JsonSerializer.Deserialize<DiscoveryResponse>(
            stringResponse, ApiClient.JsonOptions)!;

        // Assert
        response.Name.ShouldBe("LotroKoniecDev.AuthSystem");
        response.Links.Count.ShouldBe(3);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Register && l.Method == "POST");
        response.Links.ShouldContain(l => l.Rel == Rels.ForgotPassword && l.Method == "POST");
        response.Links.ShouldNotContain(l => l.Rel == Rels.ExportAccountData);
    }

    [Fact]
    public async Task Discovery_ShouldReturnAuthenticatedLinks_WhenAuthenticated()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();

        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        DiscoveryResponse response = JsonSerializer.Deserialize<DiscoveryResponse>(
            stringResponse, ApiClient.JsonOptions)!;

        // Assert
        response.Name.ShouldBe("LotroKoniecDev.AuthSystem");
        response.Links.Count.ShouldBe(2);
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.ExportAccountData && l.Method == "GET");
        response.Links.ShouldNotContain(l => l.Rel == Rels.Register);
        response.Links.ShouldNotContain(l => l.Rel == Rels.ForgotPassword);
    }

    [Fact]
    public async Task Discovery_AllLinks_ShouldHaveAbsoluteHrefs()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();
        DiscoveryResponse response = JsonSerializer.Deserialize<DiscoveryResponse>(
            stringResponse, ApiClient.JsonOptions)!;

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            link.Href.ShouldNotBeNullOrWhiteSpace();
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue(
                $"HATEOAS href for rel='{link.Rel}' must be absolute; got '{link.Href}'");
            uri!.Scheme.ShouldMatch("https?");
        }
    }

    [Fact]
    public async Task Discovery_ShouldOmitLinksProperty_WhenPlainJsonRequested()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("", UriKind.Relative));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage httpResponse = await ApiClient.Http.SendAsync(request);
        string stringResponse = await httpResponse.EnsureSuccessWithDetailsAsync();
        DiscoveryResponse response = JsonSerializer.Deserialize<DiscoveryResponse>(
            stringResponse, ApiClient.JsonOptions)!;

        // Assert
        response.Links.Count.ShouldBe(0, "plain JSON response must not carry hypermedia links");
    }
}
