using System.Collections.Generic;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class GetTranslationStatsHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    private readonly List<TranslationReadModel> _readModels = [];

    [Fact]
    public async Task Handle_WithEmptyCatalog_ShouldReturnAllZeros()
    {
        // Act
        TranslationStatsResponse stats = await HandleAsync();

        // Assert
        stats.Total.ShouldBe(0);
        stats.Translated.ShouldBe(0);
        stats.Approved.ShouldBe(0);
        stats.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WithMixedStatuses_ShouldBucketEachCounter()
    {
        // Arrange — 1 untranslated, 2 draft, 3 approved, 1 needs-review.
        Given(1, TranslationStatus.Untranslated);
        Given(2, TranslationStatus.Draft);
        Given(3, TranslationStatus.Draft);
        Given(4, TranslationStatus.Approved);
        Given(5, TranslationStatus.Approved);
        Given(6, TranslationStatus.Approved);
        Given(7, TranslationStatus.NeedsReview);

        // Act
        TranslationStatsResponse stats = await HandleAsync();

        // Assert
        stats.Total.ShouldBe(7);
        stats.Translated.ShouldBe(6);  // draft + approved + needs-review (all carry Polish)
        stats.Approved.ShouldBe(3);
        stats.Remaining.ShouldBe(4);   // total - approved
    }

    [Fact]
    public async Task Handle_ShouldExcludeSoftRemovedRowsFromEveryCounter()
    {
        // Arrange — only the kept approved row may count anywhere.
        Given(1, TranslationStatus.Approved);
        Given(2, TranslationStatus.Approved, removed: true);
        Given(3, TranslationStatus.Untranslated, removed: true);

        // Act
        TranslationStatsResponse stats = await HandleAsync();

        // Assert
        stats.Total.ShouldBe(1);
        stats.Translated.ShouldBe(1);
        stats.Approved.ShouldBe(1);
        stats.Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_WhenNoRowIsApproved_ShouldCountAllActiveRowsAsRemaining()
    {
        // Arrange
        Given(1, TranslationStatus.Untranslated);
        Given(2, TranslationStatus.Draft);
        Given(3, TranslationStatus.NeedsReview);

        // Act
        TranslationStatsResponse stats = await HandleAsync();

        // Assert
        stats.Total.ShouldBe(3);
        stats.Translated.ShouldBe(2);
        stats.Approved.ShouldBe(0);
        stats.Remaining.ShouldBe(3);
    }

    private async Task<TranslationStatsResponse> HandleAsync()
    {
        GetTranslationStats.Handler handler = new(new FakeReadDbContext(_readModels));

        Result<TranslationStatsResponse> result =
            await handler.Handle(new GetTranslationStats.Query(), CancellationToken.None);

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
}
