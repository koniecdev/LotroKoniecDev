using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class UpsertTranslationTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translations";
    private const string FileRoute = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly IdentityId Seeder = IdentityId.Create();
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public UpsertTranslationTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;");

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Upsert_OnUntranslatedRow_ShouldReturn200DraftAndStampSubmitter()
    {
        // Arrange
        await SeedAsync(gossipId: 1, source: "Welcome to Middle-earth!", SeedStatus.Untranslated);
        Guid submitter = Guid.NewGuid();
        using HttpClient client = TranslatorClient(submitter);

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 1, "Witaj w Srodziemiu!"));
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Status.ShouldBe(TranslationStatus.Draft);
        body.TranslatedText.ShouldBe("Witaj w Srodziemiu!");
        body.SubmittedById.ShouldBe(submitter);
    }

    [Fact]
    public async Task Upsert_OnApprovedRow_ShouldMoveToDraftAndRegenerateArtifact()
    {
        // Arrange — two approved rows in the distributed file; editing row 1 must pull it out (spec 0001 Q1).
        await SeedAsync(gossipId: 1, source: "One", SeedStatus.Approved, polish: "Alfa");
        await SeedAsync(gossipId: 2, source: "Two", SeedStatus.Approved, polish: "Beta");
        await RebuildArtifactAsync();
        EntityTagHeaderValue firstEtag = (await _factory.CreateClient().GetAsync(FileRoute)).Headers.ETag!;
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 1, "Alfa nowa"));
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);

        HttpResponseMessage download = await _factory.CreateClient().GetAsync(FileRoute);
        string file = await download.Content.ReadAsStringAsync();

        // Assert — the edited row is now a draft and gone from the freshly rebuilt file; row 2 stays.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!.Status.ShouldBe(TranslationStatus.Draft);
        download.Headers.ETag.ShouldNotBe(firstEtag);
        file.ShouldNotContain($"{FileId}||1||");
        file.ShouldContain($"{FileId}||2||Beta||NULL||NULL||1");
    }

    [Fact]
    public async Task Upsert_ForUnknownFragment_ShouldReturn404()
    {
        // Arrange — rows are born from import; there is no row for this pair.
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 999, "Polski"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upsert_OnRemovedRow_ShouldReturn422()
    {
        // Arrange — a soft-removed row is excluded from translation work.
        await SeedAsync(gossipId: 3, source: "Three", SeedStatus.ApprovedThenRemoved, polish: "Gamma");
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 3, "Gamma nowa"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Upsert_WithEmptyText_ShouldReturn400()
    {
        // Arrange
        await SeedAsync(gossipId: 1, source: "One", SeedStatus.Untranslated);
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 1, "   "));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upsert_WithoutToken_ShouldReturn401()
    {
        // Arrange
        await SeedAsync(gossipId: 1, source: "One", SeedStatus.Untranslated);

        // Act
        HttpResponseMessage response = await _factory.CreateClient().PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, 1, "Polski"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private enum SeedStatus
    {
        Untranslated,
        Approved,
        ApprovedThenRemoved,
    }

    private async Task SeedAsync(int gossipId, string source, SeedStatus status, string? polish = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case SeedStatus.Approved:
                row.ProvideTranslation(polish!, Seeder, Now);
                row.Approve(Seeder, Now);
                break;
            case SeedStatus.ApprovedThenRemoved:
                row.ProvideTranslation(polish!, Seeder, Now);
                row.Approve(Seeder, Now);
                row.MarkRemoved(_versionId, Now);
                break;
        }

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task RebuildArtifactAsync()
    {
        ITranslationArtifactBuilder builder = _factory.Services.GetRequiredService<ITranslationArtifactBuilder>();
        await builder.RebuildAsync("pl", CancellationToken.None);
    }

    private HttpClient TranslatorClient(Guid subject)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator, AuthConstants.Scopes.Api, subject));
        return client;
    }
}
