using LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Bootstrap;

/// <summary>
/// Proves the one-time bootstrap (#28) against real PostgreSQL: a baseline import followed by the
/// merge-only <c>polish.txt</c> seed lands the matching lines as Approved (system-attributed) and in
/// the distributed file, reports the unmatched line without creating it, and re-runs idempotently.
/// </summary>
[Collection("TranslationApi")]
public sealed class BootstrapSeedTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string FileRoute = "/api/v1/translation-files/pl";

    private readonly TranslationSystemApiFactory _factory;

    public BootstrapSeedTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bootstrap_OnEmptyDb_ImportsBaselineAndSeedsPolishAsApprovedThenIsIdempotent()
    {
        string exportedPath = WriteTempFile("exported",
            Line(1, "English one"), Line(2, "English two"), Line(3, "English three"));
        string polishPath = WriteTempFile("polish",
            Line(1, "Polski jeden"), Line(2, "Polski dwa"), Line(999, "Polski sierota"));

        BootstrapSettings settings = new()
        {
            Enabled = true,
            GameVersion = "48.0",
            ExportedTextPath = exportedPath,
            PolishTextPath = polishPath
        };

        try
        {
            // Act — first run on an empty DB.
            BootstrapReport first = await RunBootstrapAsync(settings);

            // Assert — baseline imported, two matched lines approved, the orphan reported not created.
            first.Baseline.ShouldNotBeNull();
            first.Baseline.Added.ShouldBe(3);
            first.Polish.ShouldNotBeNull();
            first.Polish.Approved.ShouldBe(2);
            first.Polish.Unmatched.ShouldBe([$"{FileId}/999"]);

            // The seed provisioned exactly one system Translator (ADR-0004), keyed by the sentinel
            // identity and carrying the system display name.
            List<Translator> translators = await LoadTranslatorsAsync();
            Translator systemTranslator = translators.ShouldHaveSingleItem();
            systemTranslator.IdentityId.ShouldBe(PolishTranslationSeeder.SystemIdentityId);
            systemTranslator.DisplayName.Value.ShouldBe(PolishTranslationSeeder.SystemDisplayName);

            // The two approved rows are stamped with that system Translator's local id; row 3 stays untranslated.
            List<Translation> rows = await LoadTranslationsAsync();
            rows.Count.ShouldBe(3);
            List<Translation> approved = rows.Where(row => row.Status == TranslationStatus.Approved).ToList();
            approved.Count.ShouldBe(2);
            approved.ShouldAllBe(row => row.SubmittedById == systemTranslator.Id);
            approved.ShouldAllBe(row => row.ApprovedById == systemTranslator.Id);

            // The distributed file carries the approved Polish, never the untranslated or orphan rows.
            HttpResponseMessage download = await _factory.CreateClient().GetAsync(FileRoute);
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            string file = await download.Content.ReadAsStringAsync();
            file.ShouldContain($"{FileId}||1||Polski jeden||NULL||NULL||1");
            file.ShouldContain($"{FileId}||2||Polski dwa||NULL||NULL||1");
            file.ShouldNotContain($"{FileId}||3||");
            file.ShouldNotContain($"{FileId}||999||");

            // Act + Assert — second run is idempotent: baseline skipped (version already exists, so
            // not re-registered), nothing re-approved, and the persisted end-state is unchanged.
            BootstrapReport second = await RunBootstrapAsync(settings);
            second.Baseline.ShouldBeNull();
            second.Polish.ShouldNotBeNull();
            second.Polish.Approved.ShouldBe(0);
            second.Polish.AlreadyApproved.ShouldBe(2);

            (await CountGameVersionsAsync()).ShouldBe(1);
            // Re-run provisions no duplicate system Translator (idempotent on IdentityId — ADR-0004).
            (await LoadTranslatorsAsync()).ShouldHaveSingleItem();
            List<Translation> afterRerun = await LoadTranslationsAsync();
            afterRerun.Count.ShouldBe(3);
            List<Translation> stillApproved = afterRerun.Where(row => row.Status == TranslationStatus.Approved).ToList();
            stillApproved.Count.ShouldBe(2);
            stillApproved.Select(row => row.TranslatedText).ShouldBe(["Polski jeden", "Polski dwa"], ignoreOrder: true);
            stillApproved.ShouldAllBe(row => row.SubmittedById == systemTranslator.Id);
            stillApproved.ShouldAllBe(row => row.ApprovedById == systemTranslator.Id);
        }
        finally
        {
            File.Delete(exportedPath);
            File.Delete(polishPath);
        }
    }

    [Fact]
    public async Task Bootstrap_WhenDisabled_IsNoOp()
    {
        BootstrapSettings settings = new() { Enabled = false };

        BootstrapReport report = await RunBootstrapAsync(settings);

        report.Baseline.ShouldBeNull();
        report.Polish.ShouldBeNull();
        (await LoadTranslationsAsync()).ShouldBeEmpty();
    }

    private async Task<BootstrapReport> RunBootstrapAsync(BootstrapSettings settings)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await TranslationsBootstrapExtensions.BootstrapTranslationsAsync(
            scope.ServiceProvider, settings, CancellationToken.None);
    }

    private async Task<List<Translation>> LoadTranslationsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translations.AsNoTracking().ToListAsync();
    }

    private async Task<List<Translator>> LoadTranslatorsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translators.AsNoTracking().ToListAsync();
    }

    private async Task<int> CountGameVersionsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.GameVersions.CountAsync();
    }

    private static string Line(int gossipId, string content) => $"{FileId}||{gossipId}||{content}||NULL||NULL||1";

    private static string WriteTempFile(string prefix, params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Join('\n', lines));
        return path;
    }
}
