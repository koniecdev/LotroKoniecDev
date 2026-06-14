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
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class ApproveTranslationTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string FileRoute = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public ApproveTranslationTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        // Seeded draft rows reference a local TranslatorId submitter (ADR-0004); the FK target must exist.
        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Approve_OnDraftRow_ShouldReturn204StampApproverAndPublish()
    {
        // Arrange — a draft row is excluded from the distributed file; approving must publish it
        // and regenerate the artifact (spec 0001).
        Guid id = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(id), null);

        TranslationDetailResponse? body = await (await client.GetAsync($"/api/v1/translations/{id}"))
            .Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        string file = await (await _factory.CreateClient().GetAsync(FileRoute)).Content.ReadAsStringAsync();

        // Assert — the approver is the lazily provisioned Translator, carrying the JWT display name.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        body.ShouldNotBeNull();
        body.Status.ShouldBe(TranslationStatus.Approved);
        body.Approver.ShouldNotBeNull();
        body.Approver.DisplayName.ShouldBe(TranslationSystemApiFactory.TestUserDisplayName);
        file.ShouldContain($"{FileId}||1||Witaj||NULL||NULL||1");
    }

    [Fact]
    public async Task Approve_OnNeedsReviewRow_ShouldClearInvalidationAndReappearInDownload()
    {
        // Arrange — an invalidated row keeps its old Polish and the superseded English; approving it
        // republishes the Polish and clears the invalidation (spec 0001).
        Guid id = await SeedAsync(gossipId: 2, SeedStatus.NeedsReview, polish: "Stary polski");
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(id), null);

        TranslationDetailResponse? body = await (await client.GetAsync($"/api/v1/translations/{id}"))
            .Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        string file = await (await _factory.CreateClient().GetAsync(FileRoute)).Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        body!.Status.ShouldBe(TranslationStatus.Approved);
        body.PreviousSourceText.ShouldBeNull();
        file.ShouldContain($"{FileId}||2||Stary polski||NULL||NULL||1");
    }

    [Fact]
    public async Task Approve_WithoutTranslation_ShouldReturn422()
    {
        // Arrange — an untranslated row has no Polish to publish.
        Guid id = await SeedAsync(gossipId: 3, SeedStatus.Untranslated, polish: null);
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(id), null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Approve_OnRemovedRow_ShouldReturn422()
    {
        // Arrange — a soft-removed row is excluded from the distributed file.
        Guid id = await SeedAsync(gossipId: 4, SeedStatus.DraftThenRemoved, polish: "Gamma");
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(id), null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Approve_ForUnknownId_ShouldReturn404()
    {
        // Arrange
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(Guid.NewGuid()), null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_AsTranslator_ShouldReturn403()
    {
        // Arrange — approval is an admin (reviewer) action; the translator role must not approve.
        Guid id = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsync(ApproveRoute(id), null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approve_WithoutToken_ShouldReturn401()
    {
        // Arrange
        Guid id = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");

        // Act
        HttpResponseMessage response = await _factory.CreateClient().PostAsync(ApproveRoute(id), null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string ApproveRoute(Guid id) => $"/api/v1/translations/{id}/approve";

    private enum SeedStatus
    {
        Untranslated,
        Draft,
        NeedsReview,
        DraftThenRemoved,
    }

    private async Task<Guid> SeedAsync(int gossipId, SeedStatus status, string? polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case SeedStatus.Draft:
                row.ProvideTranslation(polish!, _seederId, Now);
                break;
            case SeedStatus.NeedsReview:
                row.ProvideTranslation(polish!, _seederId, Now);
                row.ApplySourceChange(TranslationSource.Create("English reworded", null, null).Value, _versionId, Now);
                break;
            case SeedStatus.DraftThenRemoved:
                row.ProvideTranslation(polish!, _seederId, Now);
                row.MarkRemoved(_versionId, Now);
                break;
        }

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
        return row.Id.Value;
    }

    private HttpClient AdminClient(Guid subject)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin, AuthConstants.Scopes.Api, subject));
        return client;
    }

    private HttpClient TranslatorClient(Guid subject)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator, AuthConstants.Scopes.Api, subject));
        return client;
    }
}
