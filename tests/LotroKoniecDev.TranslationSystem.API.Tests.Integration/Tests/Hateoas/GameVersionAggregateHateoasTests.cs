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
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Hateoas;

/// <summary>
/// Pins the game-version links: for admins <c>delete</c> on any version not processed (#624),
/// <c>import</c> on any not superseded (#608) and <c>register</c> on the collection. Plain
/// <c>application/json</c> carries no links and still deserializes.
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
        await _factory.ResetDatabaseAsync(
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
    public async Task ListGameVersions_AsAdmin_ReturnsCollectionSelfRegisterAndPerItemSelfAndDelete()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(AdminClient(), Route);

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self && l.Method == "GET");
        response.Links.ShouldContain(l => l.Rel == Rels.Register && l.Method == "POST");

        GameVersionResponse item = response.Items.First(i => i.Id == id);
        item.Links.ShouldContain(l => l.Rel == Rels.Self && l.Href.Contains($"{Route}/{id.Value}"));
        item.Links.ShouldContain(l => l.Rel == Rels.Delete && l.Method == "DELETE" && l.Href.Contains($"{Route}/{id.Value}"));
    }

    [Fact]
    public async Task ListGameVersions_AsAdmin_WhenEmpty_StillCarriesCollectionLinks()
    {
        // Arrange: no versions seeded.

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
        // Arrange
        _ = await SeedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(TranslatorClient(), Route);

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Register);
    }

    [Fact]
    public async Task ListGameVersions_AsAdmin_WhenVersionIsProcessed_DoesNotAdvertiseDeleteOnTheItem()
    {
        // Arrange
        GameVersionId id = await SeedProcessedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(AdminClient(), Route);

        // Assert
        GameVersionResponse item = response.Items.First(i => i.Id == id);
        item.Links.ShouldContain(l => l.Rel == Rels.Self);
        item.Links.ShouldNotContain(l => l.Rel == Rels.Delete);
    }

    [Fact]
    public async Task ListGameVersions_AsTranslator_DoesNotAdvertiseDeleteOnTheItem()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(TranslatorClient(), Route);

        // Assert
        GameVersionResponse item = response.Items.First(i => i.Id == id);
        item.Links.ShouldContain(l => l.Rel == Rels.Self);
        item.Links.ShouldNotContain(l => l.Rel == Rels.Delete);
    }

    [Fact]
    public async Task GetGameVersion_AsAdmin_WhenUnprocessed_ReturnsSelfAndDelete()
    {
        // Arrange
        GameVersionId id = await SeedAsync("48.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            AdminClient(), $"{Route}/{id.Value}");

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldContain(l => l.Rel == Rels.Delete && l.Method == "DELETE");
    }

    [Fact]
    public async Task GetGameVersion_AsAdmin_WhenUnprocessed_AdvertisesImportAgainstTheVersion()
    {
        // Arrange: an import needs the version id, so the link is on the item (#608).
        GameVersionId id = await SeedAsync("48.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            AdminClient(), $"{Route}/{id.Value}");

        // Assert
        LinkDto import = response.Links.Where(l => l.Rel == Rels.Import).ShouldHaveSingleItem();
        import.Method.ShouldBe("POST");
        import.Href.ShouldEndWith($"{Route}/{id.Value}/import");
    }

    [Fact]
    public async Task GetGameVersion_AsAdmin_WhenProcessed_StillAdvertisesImport()
    {
        // Arrange: re-importing into a processed version is legal.
        GameVersionId id = await SeedProcessedAsync("48.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            AdminClient(), $"{Route}/{id.Value}");

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Import);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Delete);
    }

    [Fact]
    public async Task GetGameVersion_AsAdmin_WhenSuperseded_DoesNotAdvertiseImport()
    {
        // Arrange: MarkAsProcessed refuses a superseded version.
        GameVersionId id = await SeedSupersededAsync("47.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            AdminClient(), $"{Route}/{id.Value}");

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Import);
    }

    [Fact]
    public async Task GetGameVersion_AsAdmin_WhenSuperseded_AdvertisesDelete()
    {
        // Arrange: the button must be on the page, not only reachable through the API (#624).
        GameVersionId id = await SeedSupersededAsync("47.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            AdminClient(), $"{Route}/{id.Value}");

        // Assert
        LinkDto delete = response.Links.Where(l => l.Rel == Rels.Delete).ShouldHaveSingleItem();
        delete.Method.ShouldBe("DELETE");
        delete.Href.ShouldEndWith($"{Route}/{id.Value}");
    }

    [Fact]
    public async Task ListGameVersions_AsAdmin_WhenVersionIsSuperseded_AdvertisesDeleteOnTheItem()
    {
        // Arrange: the versions page renders its delete button off the list's per-item rel.
        GameVersionId id = await SeedSupersededAsync("47.0");

        // Act
        CollectionResponse<GameVersionResponse> response =
            await GetHateoasAsync<CollectionResponse<GameVersionResponse>>(AdminClient(), Route);

        // Assert
        GameVersionResponse item = response.Items.First(i => i.Id == id);
        LinkDto delete = item.Links.Where(l => l.Rel == Rels.Delete).ShouldHaveSingleItem();
        delete.Method.ShouldBe("DELETE");
        delete.Href.ShouldEndWith($"{Route}/{id.Value}");
    }

    [Fact]
    public async Task GetGameVersion_AsTranslator_DoesNotAdvertiseImport()
    {
        // Arrange: the endpoint's own policy drops the rel.
        GameVersionId id = await SeedAsync("48.0");

        // Act
        GameVersionResponse response = await GetHateoasAsync<GameVersionResponse>(
            TranslatorClient(), $"{Route}/{id.Value}");

        // Assert
        response.Links.ShouldContain(l => l.Rel == Rels.Self);
        response.Links.ShouldNotContain(l => l.Rel == Rels.Import);
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

        // Assert
        httpResponse.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.Json);
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
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);

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

    private async Task<GameVersionId> SeedProcessedAsync(string version)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, Now).Value;
        gameVersion.MarkAsProcessed();
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();

        return gameVersion.Id;
    }

    private async Task<GameVersionId> SeedSupersededAsync(string version)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, Now).Value;
        gameVersion.MarkSuperseded();
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();

        return gameVersion.Id;
    }
}
