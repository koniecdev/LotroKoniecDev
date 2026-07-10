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
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class GetTranslationStatsTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string SeederDisplayName = "Seed Author";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public GetTranslationStatsTests(TranslationSystemApiFactory factory)
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
    public async Task Stats_WithEmptyCatalog_ShouldReturnAllZeros()
    {
        // Act
        TranslationStatsResponse stats = await StatsAsync();

        // Assert
        stats.Total.ShouldBe(0);
        stats.Translated.ShouldBe(0);
        stats.Approved.ShouldBe(0);
        stats.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Stats_WithMixedStatuses_ShouldBucketEachCounter()
    {
        // Arrange — one untranslated, two drafts, three approved, one invalidated (NeedsReview).
        await SeedAsync(
            Row(1, "a"),
            Row(2, "b", TranslationStatus.Draft),
            Row(3, "c", TranslationStatus.Draft),
            Row(4, "d", TranslationStatus.Approved),
            Row(5, "e", TranslationStatus.Approved),
            Row(6, "f", TranslationStatus.Approved),
            Row(7, "g", TranslationStatus.NeedsReview));

        // Act
        TranslationStatsResponse stats = await StatsAsync();

        // Assert
        stats.Total.ShouldBe(7);
        // Translated = everything carrying Polish = drafts + approved + invalidated (all but untranslated).
        stats.Translated.ShouldBe(6);
        stats.Approved.ShouldBe(3);
        // Remaining = active rows not yet approved = Total - Approved.
        stats.Remaining.ShouldBe(4);
    }

    [Fact]
    public async Task Stats_ShouldExcludeSoftRemovedRowsFromEveryCounter()
    {
        // Arrange — a removed approved row and a removed untranslated row must not count anywhere.
        await SeedAsync(
            Row(1, "kept", TranslationStatus.Approved),
            Row(2, "removed-approved", TranslationStatus.Approved, removed: true),
            Row(3, "removed-untranslated", removed: true));

        // Act
        TranslationStatsResponse stats = await StatsAsync();

        // Assert
        stats.Total.ShouldBe(1);
        stats.Translated.ShouldBe(1);
        stats.Approved.ShouldBe(1);
        stats.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Stats_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/translations/stats");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Stats_RepeatedWithinTtl_ShouldNotRerunTheCounterQuery()
    {
        // Arrange — the first call populates the server-side cache (AUDIT-EF-04/#354).
        await SeedAsync(Row(1, "a", TranslationStatus.Approved), Row(2, "b"));
        TranslationStatsResponse first = await StatsAsync();
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        TranslationStatsResponse second = await StatsAsync();

        // Assert — served entirely from the cache: identical payload and zero read-context SQL
        // (translator provisioning touches only the write context, so the read stream stays clean).
        second.ShouldBe(first);
        _factory.ReadContextSqlRecorder.Commands.ShouldBeEmpty();
    }

    private async Task<TranslationStatsResponse> StatsAsync()
    {
        HttpResponseMessage response = await TranslatorClient().GetAsync("/api/v1/translations/stats");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TranslationStatsResponse>(JsonOptions))!;
    }

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

    private Translation Row(
        int gossipId,
        string source,
        TranslationStatus status = TranslationStatus.Untranslated,
        bool removed = false,
        int fileId = FileId)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case TranslationStatus.Draft:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                break;
            case TranslationStatus.Approved:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                row.Approve(_seederId, Now);
                break;
            case TranslationStatus.NeedsReview:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                row.ApplySourceChange(TranslationSource.Create($"{source} (reworded)", null, null).Value, _versionId, Now);
                break;
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }
}
