using System.Net.Http.Headers;
using System.Text.Json;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Hateoas;

/// <summary>
/// Pins which links the open service document sends each kind of caller (#608, ADR-0040). The sets are
/// compared whole, not with "contains", so a rel that reaches a caller who would be refused following it
/// fails the build.
/// </summary>
[Collection("TranslationApi")]
public sealed class DiscoveryHateoasTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] AnonymousRels =
    [
        Rels.Self,
        Rels.TranslationFile,
        Rels.Progress,
        Rels.Translations
    ];

    /// <summary>On top of the anonymous set.</summary>
    private static readonly string[] TranslatorOnlyRels =
    [
        Rels.Upsert,
        Rels.TranslationStats,
        Rels.GameVersions,
        Rels.ContributionDataExport
    ];

    /// <summary>On top of the translator set.</summary>
    private static readonly string[] AdminOnlyRels =
    [
        Rels.BulkApprove,
        Rels.Register
    ];

    private readonly TranslationSystemApiFactory _factory;

    public DiscoveryHateoasTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_WithoutToken_ShouldAdvertiseOnlyTheAnonymousEntryPoints()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Name.ShouldBe("LotroKoniecDev.TranslationSystem");
        response.Links.Select(link => link.Rel).OrderBy(rel => rel)
            .ShouldBe(AnonymousRels.Order());
    }

    [Fact]
    public async Task Discovery_AsTranslator_ShouldAddTheTranslatorRelsAndNoAdminRel()
    {
        // Arrange
        using HttpClient client = ClientForRole(AuthConstants.Roles.Translator);

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.Select(link => link.Rel).OrderBy(rel => rel)
            .ShouldBe(AnonymousRels.Concat(TranslatorOnlyRels).Order());
    }

    [Fact]
    public async Task Discovery_AsAdmin_ShouldAddTheAdminRelsOnTopOfTheTranslatorSet()
    {
        // Arrange
        using HttpClient client = ClientForRole(AuthConstants.Roles.Admin);

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.Select(link => link.Rel).OrderBy(rel => rel)
            .ShouldBe(AnonymousRels.Concat(TranslatorOnlyRels).Concat(AdminOnlyRels).Order());
    }

    [Fact]
    public async Task Discovery_WithAnUnrecognizedRole_ShouldSeeOnlyWhatAnAuthenticatedCallerMayReach()
    {
        // Arrange: clears RequireAuthenticatedUser but no role policy, the case a hand-written role check
        // in the factory would get wrong.
        using HttpClient client = ClientForRole("Reviewer");

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.Select(link => link.Rel).OrderBy(rel => rel)
            .ShouldBe(AnonymousRels.Append(Rels.ContributionDataExport).Order());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("unknown-key")]
    public async Task Discovery_WithARejectedToken_ShouldDegradeToTheAnonymousSetInsteadOf401(string tokenKind)
    {
        // Arrange: on the anonymous root a refused bearer is just no identity. Protected routes still
        // refuse it (AuthorizationDefaultsTests).
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokenKind is "expired"
                ? TranslationSystemApiFactory.CreateExpiredAccessToken()
                : TranslationSystemApiFactory.CreateTokenSignedWithUnknownKey());

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.Select(link => link.Rel).OrderBy(rel => rel)
            .ShouldBe(AnonymousRels.Order());
    }

    [Fact]
    public async Task Discovery_ShouldResolveTheTranslationFileRelToThePolishArtifact()
    {
        // Arrange: the rel exists so the CLI never hardcodes the path.
        using HttpClient client = _factory.CreateClient();

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        LinkDto translationFile = response.Links
            .Where(link => link.Rel == Rels.TranslationFile)
            .ShouldHaveSingleItem();
        translationFile.Method.ShouldBe("GET");
        translationFile.Href.ShouldEndWith("/api/v1/translation-files/pl");
    }

    [Fact]
    public async Task Discovery_AsAdmin_ShouldCarryTheCorrectMethodOnEveryWriteRel()
    {
        // Arrange: a client follows Method blindly.
        using HttpClient client = ClientForRole(AuthConstants.Roles.Admin);

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.ShouldContain(link => link.Rel == Rels.Upsert && link.Method == "PUT");
        response.Links.ShouldContain(link => link.Rel == Rels.BulkApprove && link.Method == "POST");
        response.Links.ShouldContain(link => link.Rel == Rels.Register && link.Method == "POST");
    }

    [Fact]
    public async Task Discovery_AllLinks_ShouldHaveAbsoluteHrefs()
    {
        // Arrange
        using HttpClient client = ClientForRole(AuthConstants.Roles.Admin);

        // Act
        DiscoveryResponse response = await GetDiscoveryAsync(client);

        // Assert
        response.Links.ShouldNotBeEmpty();
        foreach (LinkDto link in response.Links)
        {
            Uri.TryCreate(link.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue(
                $"HATEOAS href for rel='{link.Rel}' must be absolute; got '{link.Href}'");
            uri!.Scheme.ShouldMatch("https?");
        }
    }

    [Fact]
    public async Task Discovery_WhenPlainJsonRequested_ShouldOmitTheLinks()
    {
        // Arrange
        using HttpClient client = ClientForRole(AuthConstants.Roles.Admin);
        using HttpRequestMessage request = DiscoveryRequest(MediaTypes.Json);

        // Act
        using HttpResponseMessage httpResponse = await client.SendAsync(request);
        DiscoveryResponse response =
            (await httpResponse.Content.ReadFromJsonAsync<DiscoveryResponse>(JsonOptions))!;

        // Assert
        httpResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Links.Count.ShouldBe(0, "plain JSON responses must not carry hypermedia links");
    }

    private static HttpRequestMessage DiscoveryRequest(string accept)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static async Task<DiscoveryResponse> GetDiscoveryAsync(HttpClient client)
    {
        using HttpRequestMessage request = DiscoveryRequest(MediaTypes.HateoasJson);
        using HttpResponseMessage httpResponse = await client.SendAsync(request);

        httpResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        httpResponse.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);

        return (await httpResponse.Content.ReadFromJsonAsync<DiscoveryResponse>(JsonOptions))!;
    }

    private HttpClient ClientForRole(string role)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role));
        return client;
    }
}
