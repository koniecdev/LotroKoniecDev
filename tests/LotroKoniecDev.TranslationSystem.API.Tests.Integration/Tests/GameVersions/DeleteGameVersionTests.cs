using System.Net.Http.Headers;
using System.Text;
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
    public async Task Delete_ProcessedVersion_ShouldReturn422WithTheProcessedErrorCodeAndKeepIt()
    {
        // Arrange — a processed version is the one an import landed against and cannot be removed. The
        // literal errorCode is asserted on purpose: it is the wire contract the Frontend's Polish copy
        // is keyed on, and comparing a Result against its own factory would not catch a rename.
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Processed);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("GameVersionEntity.ProcessedCannotBeDeleted");
        (await ListVersionStringsAsync(client)).ShouldContain("48");
    }

    [Fact]
    public async Task Delete_SupersededVersion_ShouldReturn204AndRemoveItFromTheList()
    {
        // Arrange — superseded means "registered, then skipped": no import ever landed against it, so
        // it is exactly the row an admin must be able to clean up (#624).
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Superseded);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListVersionStringsAsync(client)).ShouldNotContain("48");
    }

    [Fact]
    public async Task Delete_SupersededVersionReferencedByATranslation_ShouldReturn422AndKeepIt()
    {
        // Arrange — the cross-aggregate net is the safety line the relaxed status guard leans on, so it
        // must still refuse a referenced version whatever its status.
        using HttpClient client = AdminClient();
        GameVersionId id = await SeedVersionAsync("48.0", GameVersionStatus.Superseded);
        await SeedTranslationReferencingAsync(id);

        // Act
        HttpResponseMessage response = await client.DeleteAsync($"{Route}/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorCodeAsync(response)).ShouldBe("GameVersionEntity.CannotDeleteReferencedVersion");
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

    [Fact]
    public async Task MistypedVersion_AfterBeingSuperseded_CanBeDeletedAndItsNumberReusedForTheRealUpdate()
    {
        // Arrange — the whole trap of #624 over the public endpoints only: a typo registers "50", the
        // real "49.2" is registered and imported, which supersedes the typo and used to seal its
        // version number for good (undeletable, un-importable, un-registrable).
        using HttpClient admin = AdminClient();

        // The supersede sweep is keyed on a strict DetectedAt `<`, stamped from the real clock at
        // registration — two HTTP round trips apart, so the typo is reliably the older row.
        Guid mistypedId = await RegisterVersionAsync(admin, "50");
        Guid realId = await RegisterVersionAsync(admin, "49.2");
        (await ImportAsync(admin, realId, Line(1, "Alpha"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetVersionAsync(admin, mistypedId)).Status.ShouldBe(GameVersionStatus.Superseded);

        // Registering the number again is still a conflict while the dead row exists — deleting it is
        // the intended way back, not a second row carrying the same version string.
        HttpResponseMessage blockedRegister =
            await admin.PostAsJsonAsync(Route, new RegisterGameVersionRequest("50"));
        blockedRegister.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Act — retire the dead row, then walk the real Update 50 in.
        HttpResponseMessage delete = await admin.DeleteAsync($"{Route}/{mistypedId}");
        Guid reRegisteredId = await RegisterVersionAsync(admin, "50");
        HttpResponseMessage import = await ImportAsync(admin, reRegisteredId, Line(1, "Alpha"), Line(2, "Beta"));

        // Assert
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        import.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetVersionAsync(admin, reRegisteredId)).Status.ShouldBe(GameVersionStatus.Processed);
        (await ListVersionStringsAsync(admin)).ShouldBe(["50", "49.2"], ignoreOrder: true);
    }

    private async Task<Guid> RegisterVersionAsync(HttpClient admin, string version)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync(Route, new RegisterGameVersionRequest(version));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        GameVersionResponse? body = await response.Content.ReadFromJsonAsync<GameVersionResponse>(JsonOptions);
        body.ShouldNotBeNull();
        return body.Id.Value;
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient admin, Guid versionId, params string[] lines)
    {
        ByteArrayContent fileContent = new(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using MultipartFormDataContent form = new() { { fileContent, "file", "exported.txt" } };

        return await admin.PostAsync($"{Route}/{versionId}/import", form);
    }

    private async Task<GameVersionResponse> GetVersionAsync(HttpClient client, Guid id)
    {
        GameVersionResponse? version = await (await client.GetAsync($"{Route}/{id}"))
            .Content.ReadFromJsonAsync<GameVersionResponse>(JsonOptions);
        version.ShouldNotBeNull();
        return version;
    }

    private static string Line(int gossipId, string text) => $"620756992||{gossipId}||{text}||NULL||NULL||1";

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.TryGetProperty("errorCode", out JsonElement code) ? code.GetString() : null;
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
