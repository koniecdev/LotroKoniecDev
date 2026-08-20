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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.ForwardedHeaders;

/// <summary>
/// Proves the TMS honours the reverse-proxy <c>X-Forwarded-*</c> headers (Program.cs
/// <c>UseForwardedHeaders</c>, ADR-0008 / M6-02): behind a TLS-terminating ingress the request
/// scheme reads <c>https</c>, so every scheme-derived absolute URL (HATEOAS hrefs, and by the same
/// mechanism the JWT issuer / OIDC redirects) is built as <c>https</c>. The HATEOAS self link is the
/// seam: it is generated from <c>HttpContext.Request.Scheme</c> via <c>LinkGenerator</c>, exactly the
/// surface forwarded headers rewrite. The suite runs in the Testing environment, where the middleware
/// is active (gated only out of Development) but <c>UseHttpsRedirection</c> is not — so the forwarded
/// proto rewrites the scheme without a redirect masking the assertion.
/// </summary>
[Collection("TranslationApi")]
public sealed class ForwardedHeadersTests : IAsyncLifetime
{
    private const string Route = "/api/v1/game-versions";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public ForwardedHeadersTests(TranslationSystemApiFactory factory)
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
    public async Task GetResource_WithForwardedProtoHttps_BuildsHttpsAbsoluteLinks()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");
        using HttpClient client = TranslatorClient();
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("https");
    }

    [Fact]
    public async Task GetResource_WithoutForwardedProto_BuildsHttpAbsoluteLinks()
    {
        // Arrange: the test server speaks plain http; with no X-Forwarded-Proto the scheme stays
        // http, proving the header (not some unrelated default) is what flips the scheme to https.
        GameVersionId id = await SeedAsync("48.0");
        using HttpClient client = TranslatorClient();
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("http");
    }

    [Fact]
    public async Task GetResource_WithForwardedHost_BuildsLinksAgainstForwardedHost()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");
        using HttpClient client = TranslatorClient();
        using HttpRequestMessage request = HateoasRequest($"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "tms.lotro-translator.pl");

        // Act
        GameVersionResponse response = await SendHateoasAsync<GameVersionResponse>(client, request);

        // Assert
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out Uri? uri).ShouldBeTrue();
        uri!.Scheme.ShouldBe("https");
        uri.Host.ShouldBe("tms.lotro-translator.pl");
    }

    [Fact]
    public async Task GetResource_WithForwardedProtoHttps_IsServedDirectlyWithoutRedirect()
    {
        // Arrange: a proxied request that already declares https must be served directly, not
        // bounced with a 3xx. NOTE: this asserts only that forwarded headers introduce no spurious
        // redirect; it does NOT exercise UseHttpsRedirection, which is gated out of the Testing
        // environment (Program.cs: `!IsDevelopment() && !IsTesting()`). The live "ForwardedProto
        // honoured first → UseHttpsRedirection is a no-op → no redirect loop" guarantee is verified
        // against a real TLS-terminating reverse proxy in the M6-07 prod-parity stack, where the
        // redirect middleware actually runs.
        GameVersionId id = await SeedAsync("48.0");
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        using HttpRequestMessage request = new(HttpMethod.Get, $"{Route}/{id.Value}");
        request.Headers.Add("X-Forwarded-Proto", "https");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
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
}
