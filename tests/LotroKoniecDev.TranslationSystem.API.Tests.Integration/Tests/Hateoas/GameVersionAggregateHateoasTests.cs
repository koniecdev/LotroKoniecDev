using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Hateoas;

/// <summary>
/// Verifies the game-version aggregate's HATEOAS links: per-item <c>self</c> (resolving to the new
/// item endpoint), the collection <c>self</c>, and the role-gated <c>register</c> action (admins
/// only). Plain <c>application/json</c> requests must carry no links and still deserialize.
/// </summary>
[Collection("TranslationApi")]
public sealed class GameVersionAggregateHateoasTests : IAsyncLifetime
{
    private const string Route = "/api/v1/game-versions";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public GameVersionAggregateHateoasTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetGameVersion_ReturnsSelfLink()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            TranslatorClient(), $"{Route}/{id.Value}");

        // Assert
        response.Links.Count.ShouldBe(1);
        LinkDto selfLink = response.Links.ShouldHaveSingleItem();
        selfLink.Rel.ShouldBe(Rels.Self);
        selfLink.Method.ShouldBe("GET");
        selfLink.Href.ShouldContain($"{Route}/{id.Value}");
        Uri.TryCreate(selfLink.Href, UriKind.Absolute, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetGameVersion_WithUnknownId_ReturnsNotFound()
    {
        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync($"{Route}/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListGameVersions_AsAdmin_ReturnsCollectionSelfRegisterAndPerItemSelf()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(AdminClient(), Route);

        // Assert — collection links (admin sees register)
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Register && l.Method == "POST");

        // Assert — each item carries its own self
        GameVersionResponse item = response.Items.First(i => i.Id == id);
        LinkDto itemSelf = item.Links.ShouldHaveSingleItem();
        itemSelf.Rel.ShouldBe(Rels.Self);
        itemSelf.Href.ShouldContain($"{Route}/{id.Value}");
    }

    [Fact]
    public async Task ListGameVersions_AsAdmin_WhenEmpty_StillCarriesCollectionLinks()
    {
        // Arrange — no versions seeded; the collection links must not depend on item count.

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(AdminClient(), Route);

        // Assert
        response.Items.ShouldBeEmpty();
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Register && l.Method == "POST");
    }

    [Fact]
    public async Task ListGameVersions_AsTranslator_DoesNotAdvertiseRegister()
    {
        // Arrange — register is the admin fallback; a translator must not see it.
        _ = await SeedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(TranslatorClient(), Route);

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Register);
    }

    [Fact]
    public async Task ListGameVersions_PlainJson_OmitsLinks()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");
        using HttpRequestMessage request = new(HttpMethod.Get, Route);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.Json));

        // Act
        HttpResponseMessage httpResponse = await AdminClient().SendAsync(request);
        CollectionResponse<GameVersionResponse> response =
            (await httpResponse.Content.ReadFromJsonAsync<CollectionResponse<GameVersionResponse>>(JsonOptions))!;

        // Assert — plain JSON still deserializes; no links anywhere.
        httpResponse.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
        response.Links.Count.ShouldBe(0);
        response.Items.First(i => i.Id == id).Links.Count.ShouldBe(0);
    }

    private async Task<T> GetHateoasAsync<T>(HttpClient client, string url) where T : class
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private HttpClient AdminClient() => ClientForRole(AuthConstants.Roles.Admin);

    private HttpClient TranslatorClient() => ClientForRole(AuthConstants.Roles.Translator);

    private HttpClient ClientForRole(string role)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role));
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
