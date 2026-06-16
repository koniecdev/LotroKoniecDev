using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.GameVersions;

[Collection("TranslationApi")]
public sealed class RegisterGameVersionTests : IAsyncLifetime
{
    private const string Route = "/api/v1/game-versions";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;

    public RegisterGameVersionTests(TranslationSystemApiFactory factory)
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
    public async Task Register_WithNewVersion_ShouldReturn201UnprocessedAndAppearInList()
    {
        // Arrange — input carries an insignificant trailing zero; it is stored canonical ("48").
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("48.0"));
        GameVersionResponse? body = await response.Content.ReadFromJsonAsync<GameVersionResponse>(JsonOptions);
        CollectionResponse<GameVersionResponse>? list = await (await client.GetAsync(Route))
            .Content.ReadFromJsonAsync<CollectionResponse<GameVersionResponse>>(JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        body.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldContain($"/api/v1/game-versions/{body.Id.Value}");
        body.Version.ShouldBe("48");
        body.Status.ShouldBe(GameVersionStatus.Unprocessed);
        list.ShouldNotBeNull();
        list.Items.ShouldContain(version => version.Version == "48");
    }

    [Fact]
    public async Task Register_DuplicateAcrossEquivalentNotations_ShouldReturn422()
    {
        // Arrange — "48" and "48.0.0" canonicalize to the same version, so the second is a conflict.
        using HttpClient client = AdminClient();
        await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("48"));

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("48.0.0"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        CollectionResponse<GameVersionResponse>? list = await (await client.GetAsync(Route))
            .Content.ReadFromJsonAsync<CollectionResponse<GameVersionResponse>>(JsonOptions);
        list.ShouldNotBeNull();
        list.Items.Count(version => version.Version == "48").ShouldBe(1);
    }

    [Fact]
    public async Task Register_WithInvalidFormat_ShouldReturn400()
    {
        // Arrange
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("banana"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_WithEmptyVersion_ShouldReturn400()
    {
        // Arrange
        using HttpClient client = AdminClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("   "));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_AsTranslator_ShouldReturn403()
    {
        // Arrange — manual registration is an admin-only fallback.
        using HttpClient client = TranslatorClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(Route, new RegisterGameVersionRequest("48.0"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().PostAsJsonAsync(Route, new RegisterGameVersionRequest("48.0"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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
