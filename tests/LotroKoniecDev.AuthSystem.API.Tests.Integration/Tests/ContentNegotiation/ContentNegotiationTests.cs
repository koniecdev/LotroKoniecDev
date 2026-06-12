using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.ContentNegotiation;

/// <summary>
/// Verifies the AuthSystem's HATEOAS content-negotiation contract on the
/// authenticated GDPR data-export endpoint, the only endpoint that currently
/// advertises hypermedia links:
/// <list type="bullet">
///   <item><c>application/vnd.dev-lotrokoniecdev.hateoas.json</c> → hypermedia links present, Content-Type matches.</item>
///   <item><c>application/json</c> → no <c>links</c> key at all, Content-Type is plain JSON.</item>
///   <item><c>*/*</c> and absent Accept → plain JSON (HATEOAS is strictly opt-in).</item>
///   <item>Quality factors resolve correctly when both media types are listed.</item>
///   <item><c>Vary: Accept</c> is always set so shared caches keep the two representations apart.</item>
/// </list>
/// </summary>
public sealed class ContentNegotiationTests : EndpointsTestBase
{
    private const string DataExportPath = "auth/account/data-export";
    private const string TestPassword = "TestPass1!";

    public ContentNegotiationTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnHateoasLinks_WhenVendorMediaTypeIsRequested()
    {
        // Arrange
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);
        response.Headers.Vary.ShouldContain("Accept");

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        JsonArray? links = body["links"]?.AsArray();
        links.ShouldNotBeNull();
        links.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAccountData_ShouldNotReturnLinksKey_WhenApplicationJsonIsRequested()
    {
        // Arrange
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
        response.Headers.Vary.ShouldContain("Accept");

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        body.AsObject().ContainsKey("links").ShouldBeFalse(
            "plain application/json must not expose the hypermedia 'links' key");
    }

    [Fact]
    public async Task ExportAccountData_ShouldNotReturnLinksKey_WhenNoAcceptHeaderIsSent()
    {
        // Arrange - a fresh client that does NOT default to the vendor media type
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpClient bareClient = Factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await bareClient.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
        response.Headers.Vary.ShouldContain("Accept");

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        body.AsObject().ContainsKey("links").ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAccountData_ShouldNotReturnLinksKey_WhenWildcardAcceptIsSent()
    {
        // Arrange - */* means "I accept anything"; HATEOAS is strictly opt-in, so we fall back to plain JSON.
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        body.AsObject().ContainsKey("links").ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnHateoas_WhenVendorTypeOutranksApplicationJsonByQValue()
    {
        // Arrange - vendor type preferred via higher q-value
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson, 1.0));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json, 0.5));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        body["links"]?.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnPlainJson_WhenApplicationJsonOutranksVendorTypeByQValue()
    {
        // Arrange - client explicitly prefers plain JSON over HATEOAS via q-values
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson, 0.3));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json, 1.0));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);

        JsonNode? body = await ReadBodyAsJsonNodeAsync(response);
        body.ShouldNotBeNull();
        body.AsObject().ContainsKey("links").ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnHateoas_WhenBothMediaTypesAreRequestedWithSameQuality()
    {
        // Arrange - ties favour the vendor (more specific, explicitly requested) type
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);
        (await ReadBodyAsJsonNodeAsync(response))!["links"]?.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAccountData_ShouldAlwaysSetVaryAcceptHeader_RegardlessOfNegotiatedRepresentation()
    {
        // Arrange
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage plainRequest = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        plainRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        plainRequest.Headers.Accept.Clear();
        plainRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        using HttpRequestMessage hateoasRequest = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        hateoasRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        hateoasRequest.Headers.Accept.Clear();
        hateoasRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        // Act
        HttpResponseMessage plainResponse = await ApiClient.Http.SendAsync(plainRequest);
        HttpResponseMessage hateoasResponse = await ApiClient.Http.SendAsync(hateoasRequest);

        // Assert - Vary: Accept is critical for cache correctness (RFC 9110 §12.5.5)
        plainResponse.Headers.Vary.ShouldContain("Accept");
        hateoasResponse.Headers.Vary.ShouldContain("Accept");
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnPlainJson_WhenVendorTypeIsExplicitlyRejectedWithZeroQuality()
    {
        // Arrange - q=0 means "absolutely not" per RFC 9110 §12.5.1
        string accessToken = await RegisterConfirmedUserAndGetTokenAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(DataExportPath, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson, 0.0));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
        (await ReadBodyAsJsonNodeAsync(response))!.AsObject().ContainsKey("links").ShouldBeFalse();
    }

    private async Task<string> RegisterConfirmedUserAndGetTokenAsync()
    {
        (RegisterRequest registerRequest, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        return await GetAccessTokenAsync(registerRequest.Username, TestPassword);
    }

    private static async Task<JsonNode?> ReadBodyAsJsonNodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonNode.ParseAsync(stream);
    }
}
