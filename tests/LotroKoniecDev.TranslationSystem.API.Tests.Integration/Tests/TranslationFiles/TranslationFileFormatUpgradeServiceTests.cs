using System.Security.Cryptography;
using System.Text;
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
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

/// <summary>
/// The catch-up ADR-0047 needs at deploy time. Otherwise the artifact is only rebuilt on the next
/// approve or import, so an updated CLI would download a six-column file and patch nothing until
/// someone happened to write.
/// It runs against a real PostgreSQL, because the whole design depends on reading only the first part of
/// the multi-MB column, and only a real database can say whether that query translates to SQL. The
/// service deliberately turns a failure there into a log line.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationFileFormatUpgradeServiceTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _submitterId;

    public TranslationFileFormatUpgradeServiceTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("49.1").Value, Now).Value;
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
    public async Task UpgradeAsync_WithAnArtifactPredatingTheColumn_ShouldRegenerateItWithTheDigest()
    {
        // Arrange: exactly the state a deploy lands in: rows already Approved, and a stored
        // artifact written by the previous release in the six-column format.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa", english: "Alpha source");
        await StoreArtifactAsync($"{FileId}||1||Alfa||NULL||NULL||1\r\n");

        // Act
        await UpgradeAsync();

        // Assert
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();
        body.ShouldBe($"{FileId}||1||Alfa||NULL||NULL||1||{SourceHash.Compute("Alpha source", null, null).ToWireDigest()}\r\n");
    }

    [Fact]
    public async Task UpgradeAsync_WithAnArtifactThatAlreadyCarriesTheColumn_ShouldLeaveItUntouched()
    {
        // Arrange: a current artifact whose Content deliberately does NOT match the Approved set.
        // If the upgrade fired it would rewrite the row, so an unchanged body proves it did not.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa", english: "Alpha source");
        string current = $"{FileId}||1||Cos calkiem innego||NULL||NULL||1||a37cc1683216cd32\r\n";
        await StoreArtifactAsync(current);

        // Act
        await UpgradeAsync();

        // Assert
        string body = await (await _factory.CreateClient().GetAsync(Route)).Content.ReadAsStringAsync();
        body.ShouldBe(current);
    }

    [Fact]
    public async Task UpgradeAsync_WithNoArtifactStoredYet_ShouldDoNothing()
    {
        // Arrange: a fresh deployment. There is nothing to upgrade, and building one here would
        // race the ordinary rebuild path for no benefit.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa", english: "Alpha source");

        // Act
        await UpgradeAsync();

        // Assert
        (await _factory.CreateClient().GetAsync(Route)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task UpgradeAsync()
    {
        TranslationFileFormatUpgradeService service = _factory.Services
            .GetServices<IHostedService>()
            .OfType<TranslationFileFormatUpgradeService>()
            .Single();

        await service.UpgradeAsync(CancellationToken.None);
    }

    private async Task StoreArtifactAsync(string content)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        dbContext.Set<PrecomputedTranslationFile>()
            .Add(PrecomputedTranslationFile.Create("pl", content, contentHash, Now));

        await dbContext.SaveChangesAsync();
    }

    private async Task SeedApprovedAsync(int gossipId, string polish, string english)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(english, null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, _submitterId, Now);
        row.Approve(_submitterId, Now);

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }
}
