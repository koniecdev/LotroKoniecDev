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
/// The xmin optimistic-concurrency token on Translation (AUDIT-EF-01). Both write paths a game
/// update's import can race — approve and upsert — are proven to LOSE to a concurrent commit rather
/// than silently overwrite it: EF's version check fails, surfacing <see cref="DbUpdateConcurrencyException"/>
/// (which the registered <c>DbUpdateConcurrencyExceptionHandler</c> maps to HTTP 409). Deterministic by
/// construction — load a snapshot, change the row out-of-band, then save the stale snapshot — because a
/// real HTTP interleave is a race; the endpoint fan-out lives in <c>ConcurrencyEndpointsTests</c>.
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

        // Attributed writes (submitter / approver) are local TranslatorId FKs (ADR-0004); the target must exist.
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
        // Arrange: a reviewer opens a Draft row to approve it (captures its version token).
        Guid id = await SeedDraftRowAsync(gossipId: 1, polish: "Witaj");

        using IServiceScope reviewerScope = _factory.Services.CreateScope();
        ApplicationWriteDbContext reviewerContext =
            reviewerScope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation reviewerCopy = await LoadTrackedAsync(reviewerContext, id);

        // A concurrent import reworded the English and parked the row for review (bumps xmin).
        await ApplySourceChangeOutOfBandAsync(id, rewordedSource: "English reworded");

        Result approveResult = reviewerCopy.Approve(_seederId, Now);
        approveResult.IsSuccess.ShouldBeTrue();

        // Act + Assert — the version check rejects the stale approve, so the import's invalidation
        // stands, unmasked (spec 0001's core invariant); the handler maps this throw to 409.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => reviewerContext.SaveChangesAsync());

        Translation persisted = await ReadFreshAsync(id);
        persisted.Status.ShouldBe(TranslationStatus.NeedsReview);
        persisted.Source.Text.ShouldBe("English reworded");
        persisted.PreviousSourceText.ShouldBe("English");
    }

    [Fact]
    public async Task Upsert_WhenAnotherTranslatorSavedFirst_ThrowsConcurrencyAndDoesNotOverwrite()
    {
        // Arrange: a translator opens an untranslated row to attach Polish (captures its version token).
        Guid id = await SeedUntranslatedRowAsync(gossipId: 2);

        using IServiceScope editorScope = _factory.Services.CreateScope();
        ApplicationWriteDbContext editorContext =
            editorScope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation editorCopy = await LoadTrackedAsync(editorContext, id);

        // Another translator committed their Polish first (bumps xmin).
        await ProvideTranslationOutOfBandAsync(id, polish: "Pierwszy polski");

        editorCopy.ProvideTranslation("Drugi polski", _seederId, Now);

        // Act + Assert — the version check rejects the stale upsert; the first writer's Polish survives.
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
