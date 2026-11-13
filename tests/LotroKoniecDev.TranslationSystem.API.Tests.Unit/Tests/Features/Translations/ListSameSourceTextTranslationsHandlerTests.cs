using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class ListSameSourceTextTranslationsHandlerTests
{
    private const int FileId = 620756996;
    private const string SourceText = "Stout-axe[D]";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    private static readonly TranslatorReadModel Saver = new(
        TranslatorId.Create(), IdentityId.Create(), "Gimli", Email: null, Now);

    private readonly List<TranslationReadModel> _readModels = [];

    [Fact]
    public async Task Handle_WhenIdIsEmpty_ShouldReturnNotFound()
    {
        // Act
        Result<SameSourceTextTranslationsResponse> result = await HandleAsync(TranslationId.Empty);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
    }

    [Fact]
    public async Task Handle_WhenRowDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        Given(1, SourceText, "Topór wojenny");

        // Act
        Result<SameSourceTextTranslationsResponse> result = await HandleAsync(TranslationId.Create());

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
    }

    [Fact]
    public async Task Handle_WhenRowIsRemoved_ShouldReturnNotFound()
    {
        // Arrange: a removed row is out of translation work and never carries the rel.
        TranslationReadModel removed = Given(1, SourceText, "Topór wojenny", removed: true);
        Given(2, SourceText, "Krzepki Topornik");

        // Act
        Result<SameSourceTextTranslationsResponse> result = await HandleAsync(removed.Id);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.NotFound");
    }

    [Fact]
    public async Task Handle_WhenTheEnglishIsUnique_ShouldReturnAnEmptyListWithoutAWarning()
    {
        // Arrange
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        Given(2, "Stout-axe[m]", "Krzepki Topornik");

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.Items.ShouldBeEmpty();
        response.TotalCount.ShouldBe(0);
        response.HasConflictingTranslations.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_WhenThreeRowsHaveDifferentPolish_ShouldListTheOtherTwoWithTheirSaverAndWarn()
    {
        // Arrange: the ticket's example.
        TranslationReadModel asked = Given(49821557, SourceText, "Topór wojenny", TranslationStatus.Approved);
        Given(127768437, SourceText, "Krzepki Topornik");
        Given(204170133, SourceText, "Wytrzymały topór");

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.TotalCount.ShouldBe(2);
        response.HasConflictingTranslations.ShouldBeTrue();
        response.Items.Select(item => item.GossipId).ShouldBe([127768437L, 204170133L]);
        response.Items.Select(item => item.TranslatedText).ShouldBe(["Krzepki Topornik", "Wytrzymały topór"]);
        response.Items.ShouldAllBe(item => item.Status == TranslationStatus.Draft);
        response.Items.ShouldAllBe(item => item.Submitter != null && item.Submitter.DisplayName == "Gimli");
    }

    [Fact]
    public async Task Handle_WhenEveryRowHasTheSamePolish_ShouldListThemWithoutAWarning()
    {
        // Arrange
        TranslationReadModel asked = Given(1, SourceText, "Krzepki Topornik");
        Given(2, SourceText, "Krzepki Topornik");
        Given(3, SourceText, "Krzepki Topornik", TranslationStatus.Approved);

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.TotalCount.ShouldBe(2);
        response.HasConflictingTranslations.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_WhenOnlyTheAskedRowDiffers_ShouldWarn()
    {
        // Arrange: the asked row's own Polish counts, or a pair of differing rows would never warn.
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        Given(2, SourceText, "Krzepki Topornik");

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.HasConflictingTranslations.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenTheOnlyTwinIsUntranslated_ShouldShowItAsUntranslatedWithoutAWarning()
    {
        // Arrange: no Polish is not a different Polish.
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        Given(2, SourceText, polish: null);

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.HasConflictingTranslations.ShouldBeFalse();
        SameSourceTextItemResponse twin = response.Items.ShouldHaveSingleItem();
        twin.TranslatedText.ShouldBeNull();
        twin.Status.ShouldBe(TranslationStatus.Untranslated);
        twin.Submitter.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_WhenAnUntranslatedRowIsAskedAndTwoTwinsDiffer_ShouldWarn()
    {
        // Arrange
        TranslationReadModel asked = Given(1, SourceText, polish: null);
        Given(2, SourceText, "Topór wojenny");
        Given(3, SourceText, "Krzepki Topornik");

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.TotalCount.ShouldBe(2);
        response.HasConflictingTranslations.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenADifferingTwinIsRemoved_ShouldLeaveItOutOfTheListAndTheWarning()
    {
        // Arrange
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        Given(2, SourceText, "Topór wojenny");
        Given(3, SourceText, "Krzepki Topornik", removed: true);

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.TotalCount.ShouldBe(1);
        response.Items.ShouldHaveSingleItem().GossipId.ShouldBe(2);
        response.HasConflictingTranslations.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_ShouldPutRowsWithPolishFirstThenOrderByFileIdAndGossipId()
    {
        // Arrange
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        Given(5, SourceText, polish: null, fileId: 100);
        Given(4, SourceText, "Topór wojenny", fileId: 200);
        Given(3, SourceText, "Topór wojenny", fileId: 100);
        Given(2, SourceText, polish: null, fileId: 50);

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.Items.Select(item => (item.FileId, item.GossipId))
            .ShouldBe([(100, 3L), (200, 4L), (50, 2L), (100, 5L)]);
    }

    [Fact]
    public async Task Handle_WhenTheGroupIsLargerThanAPage_ShouldCapTheItemsAndCountThemAll()
    {
        // Arrange: the only differing row sorts last, so it falls outside the cap and the warning still
        // covers it.
        TranslationReadModel asked = Given(1, SourceText, "Topór wojenny");
        for (int gossipId = 2; gossipId <= ListSameSourceTextTranslations.MaxItems + 10; gossipId++)
        {
            Given(gossipId, SourceText, "Topór wojenny");
        }

        Given(1000, SourceText, "Krzepki Topornik");

        // Act
        SameSourceTextTranslationsResponse response = (await HandleAsync(asked.Id)).Value;

        // Assert
        response.Items.Count.ShouldBe(ListSameSourceTextTranslations.MaxItems);
        response.TotalCount.ShouldBe(ListSameSourceTextTranslations.MaxItems + 10);
        response.HasConflictingTranslations.ShouldBeTrue();
        response.Items.ShouldNotContain(item => item.GossipId == 1000);
    }

    private async Task<Result<SameSourceTextTranslationsResponse>> HandleAsync(TranslationId id)
    {
        ListSameSourceTextTranslations.Handler handler = new(new FakeReadDbContext(_readModels));
        return await handler.Handle(new ListSameSourceTextTranslations.Query(id), CancellationToken.None);
    }

    private TranslationReadModel Given(
        long gossipId,
        string source,
        string? polish,
        TranslationStatus? status = null,
        bool removed = false,
        int fileId = FileId)
    {
        TranslationReadModel row = new(
            TranslationId.Create(),
            fileId,
            gossipId,
            source,
            null,
            null,
            polish,
            null,
            polish is null ? null : Saver.Id,
            null,
            status ?? (polish is null ? TranslationStatus.Untranslated : TranslationStatus.Draft),
            VersionId,
            null,
            removed ? VersionId : null,
            Now,
            Now)
        {
            SubmittedBy = polish is null ? null : Saver
        };
        _readModels.Add(row);
        return row;
    }
}
