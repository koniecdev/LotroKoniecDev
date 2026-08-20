using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
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

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Progress;

/// <summary>
/// The public landing-page progress endpoint (#309). Every test calls it with a token-less client —
/// anonymous access IS the contract: the landing page renders these counters before any login exists.
/// </summary>
[Collection("TranslationApi")]
public sealed class GetPublicProgressTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string SeederDisplayName = "Seed Author";
    private static readonly DateTimeOffset Now = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public GetPublicProgressTests(TranslationSystemApiFactory factory)
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
    public async Task Progress_WithEmptyCatalog_ShouldReturnZerosAndNoVersion()
    {
        // Act
        PublicProgressResponse progress = await ProgressAsync();

        // Assert
        progress.Total.ShouldBe(0);
        progress.Translated.ShouldBe(0);
        progress.Approved.ShouldBe(0);
        // The seeded base version is Unprocessed — nothing has been imported for it yet.
        progress.CurrentGameVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Progress_WithMixedStatuses_ShouldBucketEachCounter()
    {
        // Arrange: one untranslated, two drafts, three approved, one invalidated (NeedsReview).
        await SeedAsync(
            Row(1, "a"),
            Row(2, "b", TranslationStatus.Draft),
            Row(3, "c", TranslationStatus.Draft),
            Row(4, "d", TranslationStatus.Approved),
            Row(5, "e", TranslationStatus.Approved),
            Row(6, "f", TranslationStatus.Approved),
            Row(7, "g", TranslationStatus.NeedsReview));

        // Act
        PublicProgressResponse progress = await ProgressAsync();

        // Assert
        progress.Total.ShouldBe(7);
        // Translated = everything carrying Polish = drafts + approved + invalidated.
        progress.Translated.ShouldBe(6);
        progress.Approved.ShouldBe(3);
    }

    [Fact]
    public async Task Progress_ShouldExcludeSoftRemovedRowsFromEveryCounter()
    {
        // Arrange
        await SeedAsync(
            Row(1, "kept", TranslationStatus.Approved),
            Row(2, "removed-approved", TranslationStatus.Approved, removed: true),
            Row(3, "removed-untranslated", removed: true));

        // Act
        PublicProgressResponse progress = await ProgressAsync();

        // Assert
        progress.Total.ShouldBe(1);
        progress.Translated.ShouldBe(1);
        progress.Approved.ShouldBe(1);
    }

    [Fact]
    public async Task Progress_WithProcessedVersions_ShouldReturnTheNewestProcessedOne()
    {
        // Arrange: an older and a newer processed version plus a newest merely-detected one: the
        // catalog is current for the newest PROCESSED version, not the newest known version.
        await SeedVersionAsync("47.2", Now.AddDays(-30), processed: true);
        await SeedVersionAsync("48.1", Now.AddDays(-1), processed: true);
        await SeedVersionAsync("48.2", Now, processed: false);

        // Act
        PublicProgressResponse progress = await ProgressAsync();

        // Assert
        progress.CurrentGameVersion.ShouldBe("48.1");
    }

    [Fact]
    public async Task Progress_WithOnlyUnprocessedVersions_ShouldReturnNullVersion()
    {
        // Arrange: the InitializeAsync base version is already Unprocessed; add another one.
        await SeedVersionAsync("48.3", Now, processed: false);

        // Act
        PublicProgressResponse progress = await ProgressAsync();

        // Assert
        progress.CurrentGameVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Progress_RepeatedWithinTtl_ShouldNotRerunTheCounterQueries()
    {
        // Arrange: the first call populates the server-side cache (AUDIT-EF-04/#354).
        await SeedAsync(Row(1, "a", TranslationStatus.Approved), Row(2, "b"));
        PublicProgressResponse first = await ProgressAsync();
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        PublicProgressResponse second = await ProgressAsync();

        // Assert: served entirely from the cache: identical payload and zero read-context SQL
        // (the recorded command stream is the only seam that can prove "no second query").
        second.ShouldBe(first);
        _factory.ReadContextSqlRecorder.Commands.ShouldBeEmpty();
    }

    /// <summary>Anonymous by construction: the shared helper never attaches a bearer token.</summary>
    private async Task<PublicProgressResponse> ProgressAsync()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/progress");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PublicProgressResponse>(JsonOptions))!;
    }

    private async Task SeedVersionAsync(string notation, DateTimeOffset detectedAt, bool processed)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(notation).Value, detectedAt).Value;
        if (processed)
        {
            gameVersion.MarkAsProcessed().IsSuccess.ShouldBeTrue();
        }

        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
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
