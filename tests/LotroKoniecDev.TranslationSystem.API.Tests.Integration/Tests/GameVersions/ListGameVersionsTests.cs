using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.GameVersions;

[Collection("TranslationApi")]
public sealed class ListGameVersionsTests : IAsyncLifetime
{
    private const string Route = "/api/v1/game-versions";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public ListGameVersionsTests(TranslationSystemApiFactory factory)
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
    public async Task List_ReturnsVersionsNewestFirstWithStatus()
    {
        // Arrange — three versions detected on different days, with mixed lifecycle statuses.
        await SeedAsync("47.0", Now, GameVersionStatus.Superseded);
        await SeedAsync("47.1", Now.AddDays(1), GameVersionStatus.Processed);
        await SeedAsync("48.0", Now.AddDays(2), GameVersionStatus.Unprocessed);
        using HttpClient client = TranslatorClient();

        // Act
        GameVersionResponse[]? body = await (await client.GetAsync(Route))
            .Content.ReadFromJsonAsync<GameVersionResponse[]>(JsonOptions);

        // Assert — newest first; status round-trips.
        body.ShouldNotBeNull();
        body.Length.ShouldBe(3);
        body[0].Version.ShouldBe("48.0");
        body[0].Status.ShouldBe(GameVersionStatus.Unprocessed);
        body[1].Version.ShouldBe("47.1");
        body[1].Status.ShouldBe(GameVersionStatus.Processed);
        body[2].Version.ShouldBe("47.0");
        body[2].Status.ShouldBe(GameVersionStatus.Superseded);
    }

    [Fact]
    public async Task List_WhenEmpty_ReturnsEmptyArray()
    {
        // Arrange
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.GetAsync(Route);
        GameVersionResponse[]? body = await response.Content.ReadFromJsonAsync<GameVersionResponse[]>(JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task SeedAsync(string version, DateTimeOffset detectedAt, GameVersionStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, detectedAt).Value;
        switch (status)
        {
            case GameVersionStatus.Processed:
                gameVersion.MarkAsProcessed();
                break;
            case GameVersionStatus.Superseded:
                gameVersion.MarkSuperseded();
                break;
        }

        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }
}
