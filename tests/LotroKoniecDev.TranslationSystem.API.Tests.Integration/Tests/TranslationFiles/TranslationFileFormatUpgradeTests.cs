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
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

/// <summary>
/// The one-time artifact regeneration of ADR-0047 (Implementation Notes: "regeneration on deploy"),
/// driven against real PostgreSQL. The format probe projects a bounded prefix of the multi-MB
/// TOASTed content column, and whether that projection translates to SQL is exactly what a fake
/// DbContext cannot answer — while the service swallows failures by design, so a projection that did
/// not translate would be a log line and a feature that silently never fires.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationFileFormatUpgradeTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translation-files/pl";
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;

    public TranslationFileFormatUpgradeTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");
        await SeedApprovedRowAsync(gossipId: 1, english: "English", polish: "Alfa");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpgradeAsync_StoredArtifactPredatesTheDigestColumn_ShouldRegenerateItWithTheColumn()
    {
        // Arrange — an artifact written before ADR-0047: six columns, no digest. Nothing else in the
        // system schedules a rebuild on deploy, so without the upgrade every updated CLI would patch
        // nothing until the next approve.
        await StoreArtifactAsync($"{FileId}||1||Alfa||NULL||NULL||1\r\n");
        string expectedDigest = SourceHash.Compute("English", null, null).ToWireDigest();

        // Act
        await RunUpgradeAsync();

        // Assert — the distributed file now carries the row's source_digest.
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue();
        body.ShouldContain($"{FileId}||1||Alfa||NULL||NULL||1||{expectedDigest}");
    }

    [Fact]
    public async Task UpgradeAsync_StoredArtifactAlreadyCarriesTheDigestColumn_ShouldLeaveItUntouched()
    {
        // Arrange — a current artifact must not be regenerated on every start: the stored content
        // deliberately differs from what a rebuild would produce, so any regeneration is visible.
        string current = $"{FileId}||1||Alfa (stored)||NULL||NULL||1||{SourceHash.Compute("English", null, null).ToWireDigest()}\r\n";
        await StoreArtifactAsync(current);

        // Act
        await RunUpgradeAsync();

        // Assert
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(Route);
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue();
        body.ShouldContain("Alfa (stored)");
    }

    private async Task RunUpgradeAsync()
    {
        TranslationFileFormatUpgradeService service = new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IPrecomputedTranslationFileProjector>(),
            NullLogger<TranslationFileFormatUpgradeService>.Instance);
        await service.UpgradeAsync(CancellationToken.None);
    }

    private async Task StoreArtifactAsync(string content)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.PrecomputedTranslationFiles.ExecuteDeleteAsync();
        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        dbContext.PrecomputedTranslationFiles.Add(PrecomputedTranslationFile.Create("pl", content, contentHash, Now));
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedApprovedRowAsync(int gossipId, string english, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        Translator submitter = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(submitter);

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(english, null, null).Value,
            gameVersion.Id,
            Now).Value;
        row.ProvideTranslation(polish, submitter.Id, Now);
        row.Approve(submitter.Id, Now);
        dbContext.Translations.Add(row);

        await dbContext.SaveChangesAsync();
    }
}
