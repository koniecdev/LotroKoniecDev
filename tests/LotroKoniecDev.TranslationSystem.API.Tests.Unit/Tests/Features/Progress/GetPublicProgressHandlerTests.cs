using System.Collections.Generic;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Progress;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Progress;

public sealed class GetPublicProgressHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    private readonly List<TranslationReadModel> _readModels = [];
    private readonly List<GameVersionReadModel> _gameVersions = [];

    [Fact]
    public async Task Handle_WithMixedCatalog_ShouldBucketCountersAndPickTheNewestProcessedVersion()
    {
        // Arrange: 1 untranslated, 1 draft, 2 approved, 1 needs-review; the newest version is
        // merely detected, so the older processed one is the current catalog version.
        Given(1, TranslationStatus.Untranslated);
        Given(2, TranslationStatus.Draft);
        Given(3, TranslationStatus.Approved);
        Given(4, TranslationStatus.Approved);
        Given(5, TranslationStatus.NeedsReview);
        GivenVersion("48.1", Now.AddDays(-1), GameVersionStatus.Processed);
        GivenVersion("48.2", Now, GameVersionStatus.Unprocessed);

        // Act
        PublicProgressResponse progress = await HandleAsync();

        // Assert
        progress.Total.ShouldBe(5);
        progress.Translated.ShouldBe(4);
        progress.Approved.ShouldBe(2);
        progress.CurrentGameVersion.ShouldBe("48.1");
    }

    [Fact]
    public async Task Handle_SecondCallWithinTtl_ShouldServeTheSnapshotFromCacheWithoutRequerying()
    {
        // Arrange: two handlers share one cache but read different catalogs: a second database
        // read would surface the grown catalog, so an identical snapshot proves the cache served it.
        HybridCache hybridCache = TestHybridCache.Create();
        Given(1, TranslationStatus.Approved);
        GetPublicProgress.Handler first = new(TestReadScopeFactory.Create(new FakeReadDbContext(_readModels, _gameVersions)), hybridCache);
        Given(2, TranslationStatus.Draft);
        GivenVersion("48.1", Now, GameVersionStatus.Processed);
        GetPublicProgress.Handler second = new(TestReadScopeFactory.Create(new FakeReadDbContext(_readModels, _gameVersions)), hybridCache);

        // Act
        Result<PublicProgressResponse> initial =
            await first.Handle(new GetPublicProgress.Query(), CancellationToken.None);
        Result<PublicProgressResponse> cached =
            await second.Handle(new GetPublicProgress.Query(), CancellationToken.None);

        // Assert: the whole snapshot is one entry: the version lookup is deduplicated with the
        // counters, so the cached response still carries the first computation's null version.
        initial.IsSuccess.ShouldBeTrue();
        cached.IsSuccess.ShouldBeTrue();
        cached.Value.ShouldBe(initial.Value);
        cached.Value.Total.ShouldBe(1);
        cached.Value.CurrentGameVersion.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_AfterTheEntryExpires_ShouldRecomputeTheSnapshotFromTheLiveCatalog()
    {
        // Arrange: eviction is the state HybridCache reaches once the TTL lapses (the expiry timer
        // itself is HybridCache's contract); the handler must then recompute, not hold on.
        HybridCache hybridCache = TestHybridCache.Create();
        Given(1, TranslationStatus.Approved);
        GetPublicProgress.Handler first = new(TestReadScopeFactory.Create(new FakeReadDbContext(_readModels, _gameVersions)), hybridCache);
        Given(2, TranslationStatus.Draft);
        GivenVersion("48.1", Now, GameVersionStatus.Processed);
        GetPublicProgress.Handler second = new(TestReadScopeFactory.Create(new FakeReadDbContext(_readModels, _gameVersions)), hybridCache);
        Result<PublicProgressResponse> initial =
            await first.Handle(new GetPublicProgress.Query(), CancellationToken.None);
        initial.Value.Total.ShouldBe(1);

        // Act
        await hybridCache.RemoveAsync(GetPublicProgress.CounterCacheKey);
        Result<PublicProgressResponse> refreshed =
            await second.Handle(new GetPublicProgress.Query(), CancellationToken.None);

        // Assert
        refreshed.IsSuccess.ShouldBeTrue();
        refreshed.Value.Total.ShouldBe(2);
        refreshed.Value.Translated.ShouldBe(2);
        refreshed.Value.CurrentGameVersion.ShouldBe("48.1");
    }

    private async Task<PublicProgressResponse> HandleAsync()
    {
        GetPublicProgress.Handler handler =
            new(TestReadScopeFactory.Create(new FakeReadDbContext(_readModels, _gameVersions)), TestHybridCache.Create());

        Result<PublicProgressResponse> result =
            await handler.Handle(new GetPublicProgress.Query(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private void Given(int gossipId, TranslationStatus status, bool removed = false)
        => _readModels.Add(new TranslationReadModel(
            TranslationId.Create(),
            FileId,
            gossipId,
            $"source-{gossipId}",
            null,
            null,
            status == TranslationStatus.Untranslated ? null : "Polski tekst",
            null,
            null,
            null,
            status,
            VersionId,
            null,
            removed ? VersionId : null,
            Now,
            Now));

    private void GivenVersion(string notation, DateTimeOffset detectedAt, GameVersionStatus status)
        => _gameVersions.Add(new GameVersionReadModel(GameVersionId.Create(), notation, detectedAt, status));
}
