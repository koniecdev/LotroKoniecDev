using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.GameVersions;

[Collection("TranslationApi")]
public sealed class DeleteGameVersionTests : IAsyncLifetime
{
    private const string Route = "/api/v1/game-versions";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public DeleteGameVersionTests(TranslationSystemApiFactory factory)
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
    public async Task Delete_UnprocessedUnreferencedVersion_ShouldReturn204AndRemoveItFromTheList()
    {
        // Arrange
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Unprocessed);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListVersionStringsAsync(client)).ShouldNotContain("48");
    }

    [Fact]
    public async Task Delete_UnknownId_ShouldReturn404()
    {
        // Arrange
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ProcessedVersion_ShouldReturn422AndKeepIt()
    {
        // Arrange — a processed version is woven into the lifecycle and cannot be removed.
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Processed);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ListVersionStringsAsync(client)).ShouldContain("48");
    }

    [Fact]
    public async Task Delete_SupersededVersion_ShouldReturn422AndKeepIt()
    {
        // Arrange — a superseded version is also outside the deletable (unprocessed) state.
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Superseded);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ListVersionStringsAsync(client)).ShouldContain("48");
    }

    [Fact]
    public async Task Delete_VersionReferencedByATranslation_ShouldReturn422AndKeepIt()
    {
        // Arrange — an unprocessed version that a translation references (the defense-in-depth guard,
        // exercised against real PostgreSQL so AnyReferencesGameVersionAsync is proven to translate).
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Unprocessed);
        await SeedTranslationReferencingAsync(id);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ListVersionStringsAsync(client)).ShouldContain("48");
    }

    [Fact]
    public async Task Delete_AsTranslator_ShouldReturn403()
    {
        // Arrange — deletion is an admin-only action.
        using HttpClient adminClient = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Unprocessed);

        // Act
        HttpResponseMessage response = await TranslatorClient().DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().DeleteAsync($"{Route}/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<string[]> ListVersionStringsAsync(HttpClient client)
    {
        CollectionResponse<GameVersionResponse>? list = await (await client.GetAsync(Route))
            .Content.ReadFromJsonAsync<CollectionResponse<GameVersionResponse>>(JsonOptions);
        list.ShouldNotBeNull();
        return list.Items.Select(version => version.Version).ToArray();
    }

    private async Task<GameVersionId> SeedVersionAsync(string version, GameVersionStatus status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, Now).Value;
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

        return gameVersion.Id;
    }

    private async Task SeedTranslationReferencingAsync(GameVersionId versionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(620756992, 1001).Value,
            TranslationSource.Create("Witaj", null, null).Value,
            versionId,
            Now).Value;

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private HttpClient AdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));
        return client;
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }
}
