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
/// Pins that the TMS reads the proxy's <c>X-Forwarded-*</c> headers (ADR-0008), seen through the
/// HATEOAS self link, which is built from the request scheme. Testing has the middleware on but
/// <c>UseHttpsRedirection</c> off, so no redirect hides the result.
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
        // Arrange: proves the header, not some default, is what makes the scheme https.
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
        // Arrange: proves only that forwarded headers add no redirect. UseHttpsRedirection is off in
        // Testing; the no-redirect-loop guarantee is checked on the prod-parity stack (M6-07).
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
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);

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
