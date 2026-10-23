using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Concurrency;

/// <summary>
/// Pins the xmin token (AUDIT-EF-01): an approve or upsert built on an old copy loses to a commit in
/// between with <see cref="DbUpdateConcurrencyException"/> (a 409 over HTTP). Load, change out of band,
/// save the old copy: repeatable, unlike a real race (see <c>ConcurrencyEndpointsTests</c>).
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationConcurrencyTokenTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public TranslationConcurrencyTokenTests(TranslationSystemApiFactory factory)
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

        // The submitter's foreign key target must exist (ADR-0004).
        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Approve_WhenAConcurrentImportInvalidatesTheRowFirst_ThrowsConcurrencyAndKeepsTheInvalidation()
    {
        // Arrange
        Guid id = await SeedDraftRowAsync(gossipId: 1, polish: "Witaj");

        using IServiceScope reviewerScope = _factory.Services.CreateScope();
        ApplicationWriteDbContext reviewerContext =
            reviewerScope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation reviewerCopy = await LoadTrackedAsync(reviewerContext, id);

        // A concurrent import rewords the English.
        await ApplySourceChangeOutOfBandAsync(id, rewordedSource: "English reworded");

        Result approveResult = reviewerCopy.Approve(_seederId, Now);
        approveResult.IsSuccess.ShouldBeTrue();

        // Act and assert: the import's invalidation must stand (spec 0001).
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => reviewerContext.SaveChangesAsync());

        Translation persisted = await ReadFreshAsync(id);
        persisted.Status.ShouldBe(TranslationStatus.NeedsReview);
        persisted.Source.Text.ShouldBe("English reworded");
        persisted.PreviousSourceText.ShouldBe("English");
    }

    [Fact]
    public async Task Upsert_WhenAnotherTranslatorSavedFirst_ThrowsConcurrencyAndDoesNotOverwrite()
    {
        // Arrange
        Guid id = await SeedUntranslatedRowAsync(gossipId: 2);

        using IServiceScope editorScope = _factory.Services.CreateScope();
        ApplicationWriteDbContext editorContext =
            editorScope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation editorCopy = await LoadTrackedAsync(editorContext, id);

        // Another translator commits first.
        await ProvideTranslationOutOfBandAsync(id, polish: "Pierwszy polski");

        editorCopy.ProvideTranslation("Drugi polski", _seederId, Now);

        // Act and assert
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => editorContext.SaveChangesAsync());

        Translation persisted = await ReadFreshAsync(id);
        persisted.TranslatedText.ShouldBe("Pierwszy polski");
        persisted.Status.ShouldBe(TranslationStatus.Draft);
    }

    private static Task<Translation> LoadTrackedAsync(ApplicationWriteDbContext dbContext, Guid id)
    {
        TranslationId translationId = TranslationId.FromValue(id);
        return dbContext.Translations.SingleAsync(translation => translation.Id == translationId);
    }

    private async Task ApplySourceChangeOutOfBandAsync(Guid id, string rewordedSource)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = await LoadTrackedAsync(dbContext, id);
        row.ApplySourceChange(TranslationSource.Create(rewordedSource, null, null).Value, _versionId, Now);
        await dbContext.SaveChangesAsync();
    }

    private async Task ProvideTranslationOutOfBandAsync(Guid id, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = await LoadTrackedAsync(dbContext, id);
        row.ProvideTranslation(polish, _seederId, Now);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Translation> ReadFreshAsync(Guid id)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        TranslationId translationId = TranslationId.FromValue(id);
        return await dbContext.Translations.AsNoTracking().SingleAsync(translation => translation.Id == translationId);
    }

    private async Task<Guid> SeedDraftRowAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, _seederId, Now);
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
        return row.Id.Value;
    }

    private async Task<Guid> SeedUntranslatedRowAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
        return row.Id.Value;
    }
}
