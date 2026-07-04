using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class BulkApproveTranslationsTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string BulkApproveRoute = "/api/v1/translations/approve";
    private const string FileRoute = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public BulkApproveTranslationsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BulkApprove_OnDraftAndNeedsReviewRows_ShouldApproveBothAndPublish()
    {
        // Arrange — both a plain draft and an invalidated (NeedsReview) row are approvable; approving
        // publishes both and rebuilds the distributed artifact (spec 0001, #322).
        Guid draftId = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");
        Guid needsReviewId = await SeedAsync(gossipId: 2, SeedStatus.NeedsReview, polish: "Stary polski");
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([draftId, needsReviewId]), JsonOptions);
        BulkApproveTranslationsResponse? summary =
            await response.Content.ReadFromJsonAsync<BulkApproveTranslationsResponse>(JsonOptions);

        (_, string file) = await TranslationFileDownloadPolling.DownloadWhenConvergedAsync(
            _factory.CreateClient(),
            FileRoute,
            (download, content) => download.IsSuccessStatusCode
                && content.Contains($"{FileId}||1||Witaj||NULL||NULL||1")
                && content.Contains($"{FileId}||2||Stary polski||NULL||NULL||1"));

        // Assert
        response.EnsureSuccessStatusCode();
        summary.ShouldNotBeNull();
        summary.Requested.ShouldBe(2);
        summary.Approved.ShouldBe(2);
        summary.Skipped.ShouldBe(0);
        file.ShouldContain($"{FileId}||1||Witaj||NULL||NULL||1");
        file.ShouldContain($"{FileId}||2||Stary polski||NULL||NULL||1");
    }

    [Fact]
    public async Task BulkApprove_WithApprovableUntranslatedAndUnknownIds_ShouldApproveOnlyTheApprovableOne()
    {
        // Arrange — best-effort: a Draft is approved, an Untranslated row and an unknown id are skipped;
        // one stale/invalid id must never fail the whole batch.
        Guid draftId = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");
        Guid untranslatedId = await SeedAsync(gossipId: 2, SeedStatus.Untranslated, polish: null);
        Guid unknownId = Guid.NewGuid();
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([draftId, untranslatedId, unknownId]), JsonOptions);
        BulkApproveTranslationsResponse? summary =
            await response.Content.ReadFromJsonAsync<BulkApproveTranslationsResponse>(JsonOptions);

        // Assert
        response.EnsureSuccessStatusCode();
        summary.ShouldNotBeNull();
        summary.Requested.ShouldBe(3);
        summary.Approved.ShouldBe(1);
        summary.Skipped.ShouldBe(2);
    }

    [Fact]
    public async Task BulkApprove_WhenNoRowIsApprovable_ShouldReturnZeroApproved()
    {
        // Arrange — an untranslated row has no Polish to publish; the request is well-formed but
        // publishes nothing, which is still a success (200, approved:0).
        Guid untranslatedId = await SeedAsync(gossipId: 1, SeedStatus.Untranslated, polish: null);
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([untranslatedId]), JsonOptions);
        BulkApproveTranslationsResponse? summary =
            await response.Content.ReadFromJsonAsync<BulkApproveTranslationsResponse>(JsonOptions);

        // Assert
        response.EnsureSuccessStatusCode();
        summary.ShouldNotBeNull();
        summary.Approved.ShouldBe(0);
        summary.Skipped.ShouldBe(1);
    }

    [Fact]
    public async Task BulkApprove_WithEmptyIds_ShouldReturn400()
    {
        // Arrange
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([]), JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkApprove_WithMoreThanOneHundredIds_ShouldReturn400()
    {
        // Arrange — the cap mirrors the list's max page size.
        Guid[] ids = [.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())];
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest(ids), JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkApprove_AsTranslator_ShouldReturn403()
    {
        // Arrange — bulk approve is an admin (reviewer) action; the translator role must not approve.
        Guid draftId = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([draftId]), JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BulkApprove_WithoutToken_ShouldReturn401()
    {
        // Arrange
        Guid draftId = await SeedAsync(gossipId: 1, SeedStatus.Draft, polish: "Witaj");

        // Act
        HttpResponseMessage response = await _factory.CreateClient().PostAsJsonAsync(
            BulkApproveRoute, new BulkApproveTranslationsRequest([draftId]), JsonOptions);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BulkApprove_WithNullIdsBody_ShouldReturn400()
    {
        // Arrange — a raw body whose ids field is null (or a missing field) binds Ids to null; the endpoint
        // must normalize it to an empty list and fail validation with 400, never 500 from a null dereference.
        using HttpClient client = AdminClient(Guid.NewGuid());
        using StringContent body = new("""{"ids":null}""");
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Act
        HttpResponseMessage response = await client.PostAsync(BulkApproveRoute, body);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private enum SeedStatus
    {
        Untranslated,
        Draft,
        NeedsReview,
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
