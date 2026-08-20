using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

/// <summary>
/// Pins the projector's write shape against real PostgreSQL (PERF-04/#289): the first build per
/// language inserts the artifact row; every later rebuild refreshes it with one set-based UPDATE
/// and never re-fetches the previous multi-MB content. The DB command stream is the only
/// only way to see that "the old content was not loaded", the same approach PERF-01 uses.
/// </summary>
[Collection("TranslationApi")]
public sealed class PrecomputedTranslationFileProjectorTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _submitterId;

    public PrecomputedTranslationFileProjectorTests(TranslationSystemApiFactory factory)
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

        Translator submitter = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(submitter);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _submitterId = submitter.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Rebuild_OnFirstBuild_ShouldInsertTheArtifactRow()
    {
        // Arrange
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        _factory.WriteContextSqlRecorder.Clear();

        // Act
        await RebuildAsync();

        // Assert: no row existed, so the set-based refresh missed and the first build inserted.
        HttpResponseMessage download = await _factory.CreateClient().GetAsync(Route);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        _factory.WriteContextSqlRecorder.Commands
            .ShouldContain(command => command.Contains("INSERT INTO translation.\"TranslationArtifacts\""));
    }

    [Fact]
    public async Task Rebuild_WhenArtifactExists_ShouldRefreshInPlaceWithoutFetchingPreviousContent()
    {
        // Arrange: the artifact already exists from a first build.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        await SeedApprovedAsync(gossipId: 2, polish: "Beta");
        _factory.WriteContextSqlRecorder.Clear();

        // Act
        await RebuildAsync();

        // Assert: one set-based UPDATE refreshed the row (PERF-04); nothing on the write context
        // SELECTed the artifact table, so the previous multi-MB Content was never materialized
        // just to be overwritten.
        HttpResponseMessage download = await _factory.CreateClient().GetAsync(Route);
        string body = await download.Content.ReadAsStringAsync();
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1");
        body.ShouldContain($"{FileId}||2||Beta||NULL||NULL||1");

        IReadOnlyList<string> commands = _factory.WriteContextSqlRecorder.Commands;
        commands.ShouldContain(command => command.Contains("UPDATE translation.\"TranslationArtifacts\""));
        commands.ShouldAllBe(command =>
            !(command.Contains("SELECT") && command.Contains("\"TranslationArtifacts\"")));
        commands.ShouldAllBe(command => !command.Contains("INSERT INTO translation.\"TranslationArtifacts\""));
    }

    [Fact]
    public async Task Rebuild_ShouldStampEachRowWithTheDigestOfItsEnglishSourceNotItsPolish()
    {
        // Arrange: the row ships Polish, but the seventh column must describe the ENGLISH it was
        // approved against (ADR-0047 §2). Getting this backwards is invisible in the file's shape
        // and would make every pristine fragment on every player's box report "source moved".
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa", english: "Alpha source");

        // Act
        await RebuildAsync();

        // Assert
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();
        string englishDigest = SourceHash.Compute("Alpha source", null, null).ToWireDigest();

        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1||{englishDigest}\r\n");
        body.ShouldNotContain(SourceHash.Compute("Alfa", null, null).ToWireDigest());
    }

    [Fact]
    public async Task Rebuild_ForARowWithArgumentColumns_ShouldHashTheSourcesOwnArgumentColumns()
    {
        // Arrange: the triple is (text, args_order, args_id), so a row whose placeholder structure
        // differs must land on a different digest even when the text matches.
        await SeedApprovedAsync(gossipId: 2, polish: "Beta", english: "Beta source", argsOrder: "1-2", argsId: "1-2");

        // Act
        await RebuildAsync();

        // Assert
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();

        body.ShouldContain(
            $"{FileId}||2||Beta||1-2||1-2||1||{SourceHash.Compute("Beta source", "1-2", "1-2").ToWireDigest()}\r\n");
    }

    private async Task SeedApprovedAsync(
        int gossipId,
        string polish,
        string english = "English",
        string? argsOrder = null,
        string? argsId = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(english, argsOrder, argsId).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, _submitterId, Now);
        row.Approve(_submitterId, Now);

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task RebuildAsync()
    {
        IPrecomputedTranslationFileProjector projector =
            _factory.Services.GetRequiredService<IPrecomputedTranslationFileProjector>();
        await projector.RebuildAsync("pl", CancellationToken.None);
    }
}
