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
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

/// <summary>
/// The editor's same-text panel (#1347) against real PostgreSQL, where the grouped count with a distinct
/// inside must translate to SQL.
/// </summary>
[Collection("TranslationApi")]
public sealed class ListSameSourceTextTranslationsTests : IAsyncLifetime
{
    private const int FileId = 620756996;
    private const string SourceText = "Stout-axe[D]";
    private const string SeederDisplayName = "Seed Author";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public ListSameSourceTextTranslationsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create(SeederDisplayName).Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_ForTheTicketsExampleRows_ShouldListTheOtherTwoWithTheirSaverAndWarn()
    {
        // Arrange: the three rows of #1347, one approved and two drafts, each with other Polish.
        Translation approved = Row(49821557, SourceText, "Topór wojenny", approve: true);
        await SeedAsync(
            approved,
            Row(127768437, SourceText, "Krzepki Topornik"),
            Row(204170133, SourceText, "Wytrzymały topór"),
            Row(1, "Dwarf[D]", "Krasnolud"));

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync(SameSourceTextUri(approved.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SameSourceTextTranslationsResponse body =
            (await response.Content.ReadFromJsonAsync<SameSourceTextTranslationsResponse>(JsonOptions))!;
        body.TotalCount.ShouldBe(2);
        body.HasConflictingTranslations.ShouldBeTrue();
        body.Items.Select(item => (item.GossipId, item.TranslatedText, item.Status))
            .ShouldBe([
                (127768437L, "Krzepki Topornik", TranslationStatus.Draft),
                (204170133L, "Wytrzymały topór", TranslationStatus.Draft)
            ]);
        body.Items.ShouldAllBe(item => item.FileId == FileId);
        body.Items.ShouldAllBe(item => item.Submitter != null && item.Submitter.DisplayName == SeederDisplayName);
    }

    [Fact]
    public async Task Get_WhenTheEnglishIsUnique_ShouldReturnAnEmptyList()
    {
        // Arrange: a text that differs only in its grammar tag is another text.
        Translation row = Row(1, SourceText, "Topór wojenny");
        await SeedAsync(row, Row(2, "Stout-axe[m]", "Krzepki Topornik"));

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync(SameSourceTextUri(row.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SameSourceTextTranslationsResponse body =
            (await response.Content.ReadFromJsonAsync<SameSourceTextTranslationsResponse>(JsonOptions))!;
        body.Items.ShouldBeEmpty();
        body.TotalCount.ShouldBe(0);
        body.HasConflictingTranslations.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_WhenTwinsAgreeOrAreUntranslated_ShouldNotWarn()
    {
        // Arrange: an untranslated twin has no Polish, which is not a different Polish.
        Translation row = Row(1, SourceText, "Topór wojenny");
        await SeedAsync(row, Row(2, SourceText, "Topór wojenny"), Row(3, SourceText, polish: null));

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync(SameSourceTextUri(row.Id));

        // Assert
        SameSourceTextTranslationsResponse body =
            (await response.Content.ReadFromJsonAsync<SameSourceTextTranslationsResponse>(JsonOptions))!;
        body.TotalCount.ShouldBe(2);
        body.HasConflictingTranslations.ShouldBeFalse();
        body.Items.Select(item => item.TranslatedText).ShouldBe(["Topór wojenny", null]);
        body.Items.Last().Status.ShouldBe(TranslationStatus.Untranslated);
    }

    [Fact]
    public async Task Get_WhenTheDifferingTwinIsRemoved_ShouldLeaveItOut()
    {
        // Arrange
        Translation row = Row(1, SourceText, "Topór wojenny");
        await SeedAsync(row, Row(2, SourceText, "Krzepki Topornik", removed: true));

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync(SameSourceTextUri(row.Id));

        // Assert
        SameSourceTextTranslationsResponse body =
            (await response.Content.ReadFromJsonAsync<SameSourceTextTranslationsResponse>(JsonOptions))!;
        body.TotalCount.ShouldBe(0);
        body.Items.ShouldBeEmpty();
        body.HasConflictingTranslations.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_WhenTheRowIsRemoved_ShouldReturn404()
    {
        // Arrange
        Translation removed = Row(1, SourceText, "Topór wojenny", removed: true);
        await SeedAsync(removed, Row(2, SourceText, "Krzepki Topornik"));

        // Act
        HttpResponseMessage response = await TranslatorClient().GetAsync(SameSourceTextUri(removed.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_WithUnknownId_ShouldReturn404()
    {
        // Act
        HttpResponseMessage response =
            await TranslatorClient().GetAsync($"/api/v1/translations/{Guid.NewGuid()}/same-source-text");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_WithoutToken_ShouldReturn401()
    {
        // Arrange
        Translation row = Row(1, SourceText, "Topór wojenny");
        await SeedAsync(row);

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(SameSourceTextUri(row.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WithoutTheTranslatorRole_ShouldReturn403()
    {
        // Arrange
        Translation row = Row(1, SourceText, "Topór wojenny");
        await SeedAsync(row);
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role: "Reader"));

        // Act
        HttpResponseMessage response = await client.GetAsync(SameSourceTextUri(row.Id));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string SameSourceTextUri(TranslationId id) =>
        $"/api/v1/translations/{id.Value}/same-source-text";

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }

    private async Task SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
    }

    private Translation Row(long gossipId, string source, string? polish, bool approve = false, bool removed = false)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        if (polish is not null)
        {
            row.ProvideTranslation(polish, _seederId, Now);
        }

        if (approve)
        {
            row.Approve(_seederId, Now);
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }
}
