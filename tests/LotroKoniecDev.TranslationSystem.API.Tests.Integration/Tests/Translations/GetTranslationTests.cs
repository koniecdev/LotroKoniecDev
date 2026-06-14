using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class GetTranslationTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public GetTranslationTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\" CASCADE;");

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_WithExistingId_ShouldReturn200AndPayload()
    {
        // Arrange
        TranslationId id = await SeedAsync(gossipId: 1001, source: "Witaj w Srodziemiu!");

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        body.ShouldNotBeNull();
        body.Id.ShouldBe(id);
        body.FileId.ShouldBe(FileId);
        body.GossipId.ShouldBe(1001);
        body.SourceText.ShouldBe("Witaj w Srodziemiu!");
        body.Status.ShouldBe(TranslationStatus.Untranslated);
    }

    [Fact]
    public async Task Get_WithInvalidatedRow_ShouldExposePreviousSourceArgsAndTranslation()
    {
        // Arrange — a row that carried Polish, then a game update reworded its source: NeedsReview,
        // with the args columns and the superseded English preserved for side-by-side review. These
        // are the detail endpoint's reason to exist beyond the list item; distinct args values also
        // pin the projection's column order (a swapped ArgsOrder/ArgsId would fail here).
        TranslationId id = await SeedInvalidatedAsync();

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        body.ShouldNotBeNull();
        body.Status.ShouldBe(TranslationStatus.NeedsReview);
        body.SourceText.ShouldBe("Reworded source");
        body.PreviousSourceText.ShouldBe("Original source");
        body.TranslatedText.ShouldBe("Polski tekst");
        body.ArgsOrder.ShouldBe("1-2");
        body.ArgsId.ShouldBe("3-4");
    }

    [Fact]
    public async Task Get_WithUnknownId_ShouldReturn404()
    {
        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_WithoutToken_ShouldReturn401()
    {
        // Arrange
        TranslationId id = await SeedAsync(gossipId: 1001, source: "Witaj");

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync($"/api/v1/translations/{id.Value}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }

    private async Task<TranslationId> SeedAsync(int gossipId, string source)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
        return row.Id;
    }

    private async Task<TranslationId> SeedInvalidatedAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, 2002).Value,
            TranslationSource.Create("Original source", "1-2", "3-4").Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation("Polski tekst", IdentityId.Create(), Now);
        row.ApplySourceChange(TranslationSource.Create("Reworded source", "1-2", "3-4").Value, _versionId, Now);
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
        return row.Id;
    }
}
