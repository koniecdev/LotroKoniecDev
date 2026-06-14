using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationTests
{
    private static readonly DateTimeOffset Created = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Changed = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId IntroducedVersion = GameVersionId.Create();
    private static readonly GameVersionId ChangeVersion = GameVersionId.Create();
    private static readonly TranslatorId Submitter = TranslatorId.Create();
    private static readonly TranslatorId Approver = TranslatorId.Create();

    private static FragmentKey Key() => FragmentKey.Create(620756992, 1001).Value;
    private static TranslationSource Source(string text) => TranslationSource.Create(text, null, null).Value;

    private static Translation CreateUntranslated()
        => Translation.CreateUntranslated(Key(), Source("Old English"), IntroducedVersion, Created).Value;

    [Fact]
    public void CreateUntranslated_WithValidInputs_ShouldStartUntranslated()
    {
        // Act
        Result<Translation> result = Translation.CreateUntranslated(Key(), Source("Hello"), IntroducedVersion, Created);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        Translation translation = result.Value;
        translation.Status.ShouldBe(TranslationStatus.Untranslated);
        translation.TranslatedText.ShouldBeNull();
        translation.IntroducedInVersion.ShouldBe(IntroducedVersion);
        translation.IsRemoved.ShouldBeFalse();
        translation.CreatedAt.ShouldBe(Created);
        translation.UpdatedAt.ShouldBe(Created);
    }

    [Fact]
    public void ApplySourceChange_WhenUntranslated_ShouldUpdateSourceWithoutInvalidating()
    {
        // Arrange
        Translation translation = CreateUntranslated();

        // Act
        translation.ApplySourceChange(Source("New English"), ChangeVersion, Changed);

        // Assert
        translation.Source.Text.ShouldBe("New English");
        translation.Status.ShouldBe(TranslationStatus.Untranslated);
        translation.PreviousSourceText.ShouldBeNull();
        translation.LastSourceChangeInVersion.ShouldBe(ChangeVersion);
        translation.UpdatedAt.ShouldBe(Changed);
    }

    [Fact]
    public void ApplySourceChange_WhenPolishExists_ShouldInvalidateAndKeepPreviousSource()
    {
        // Arrange
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Stary polski", Submitter, Created);

        // Act
        translation.ApplySourceChange(Source("New English"), ChangeVersion, Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.NeedsReview);
        translation.PreviousSourceText.ShouldBe("Old English");
        translation.Source.Text.ShouldBe("New English");
        translation.TranslatedText.ShouldBe("Stary polski");
        translation.LastSourceChangeInVersion.ShouldBe(ChangeVersion);
    }

    [Fact]
    public void ApplySourceChange_OnAlreadyNeedsReviewRow_ShouldKeepFirstSupersededSource()
    {
        // Arrange — a row reworded by several updates before anyone reviews it (spec 0001): the
        // superseded English must stay pinned to what the still-current Polish was written against,
        // not drift to an intermediate source the translator never translated.
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Stary polski", Submitter, Created);
        translation.ApplySourceChange(Source("Reworded once"), ChangeVersion, Changed);

        // Act
        translation.ApplySourceChange(Source("Reworded twice"), ChangeVersion, Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.NeedsReview);
        translation.PreviousSourceText.ShouldBe("Old English");
        translation.Source.Text.ShouldBe("Reworded twice");
        translation.TranslatedText.ShouldBe("Stary polski");
    }

    [Fact]
    public void ApplySourceChange_AfterReDraftingInvalidatedRow_ShouldRefreshPreviousSource()
    {
        // Arrange — translator re-translates an invalidated row against its new English, then a later
        // update rewords it again: the superseded English must now track the re-drafted baseline, not
        // the original (spec 0001).
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Stary polski", Submitter, Created);
        translation.ApplySourceChange(Source("Reworded once"), ChangeVersion, Changed);
        translation.ProvideTranslation("Nowy polski", Submitter, Changed);

        // Act
        translation.ApplySourceChange(Source("Reworded twice"), ChangeVersion, Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.NeedsReview);
        translation.PreviousSourceText.ShouldBe("Reworded once");
        translation.Source.Text.ShouldBe("Reworded twice");
        translation.TranslatedText.ShouldBe("Nowy polski");
    }

    [Fact]
    public void MarkRemoved_ShouldSoftMarkWithVersion()
    {
        // Arrange
        Translation translation = CreateUntranslated();

        // Act
        translation.MarkRemoved(ChangeVersion, Changed);

        // Assert
        translation.IsRemoved.ShouldBeTrue();
        translation.RemovedInVersion.ShouldBe(ChangeVersion);
        translation.UpdatedAt.ShouldBe(Changed);
    }

    [Fact]
    public void Restore_ShouldClearRemovalAndKeepStatus()
    {
        // Arrange
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Polski", Submitter, Created);
        translation.MarkRemoved(ChangeVersion, Changed);

        // Act
        translation.Restore(Changed);

        // Assert
        translation.IsRemoved.ShouldBeFalse();
        translation.RemovedInVersion.ShouldBeNull();
        translation.Status.ShouldBe(TranslationStatus.Draft);
        translation.TranslatedText.ShouldBe("Polski");
    }

    [Fact]
    public void ApplySourceChange_WhenRemoved_ShouldClearRemoval()
    {
        // Arrange — a re-added pair whose source differs lands here (spec 0001).
        Translation translation = CreateUntranslated();
        translation.MarkRemoved(ChangeVersion, Changed);

        // Act
        translation.ApplySourceChange(Source("Reworded"), ChangeVersion, Changed);

        // Assert
        translation.IsRemoved.ShouldBeFalse();
        translation.Source.Text.ShouldBe("Reworded");
    }

    [Fact]
    public void ProvideTranslation_ShouldAttachDraft()
    {
        // Arrange
        Translation translation = CreateUntranslated();

        // Act
        translation.ProvideTranslation("Witaj", Submitter, Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.Draft);
        translation.TranslatedText.ShouldBe("Witaj");
        translation.SubmittedById.ShouldBe(Submitter);
        translation.UpdatedAt.ShouldBe(Changed);
    }

    [Fact]
    public void ProvideTranslation_OnNeedsReviewRow_ShouldReturnToDraftAndKeepPreviousSource()
    {
        // Arrange — an invalidated row (a game update reworded its source): re-translating it is the
        // #100 re-translation path. The superseded English must stay until approve clears it.
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Stary polski", Submitter, Created);
        translation.ApplySourceChange(Source("New English"), ChangeVersion, Changed);
        translation.Status.ShouldBe(TranslationStatus.NeedsReview);
        TranslatorId reviewer = TranslatorId.Create();

        // Act
        translation.ProvideTranslation("Nowy polski", reviewer, Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.Draft);
        translation.TranslatedText.ShouldBe("Nowy polski");
        translation.PreviousSourceText.ShouldBe("Old English");
        translation.SubmittedById.ShouldBe(reviewer);
    }

    [Fact]
    public void Approve_WhenDraft_ShouldSetApprovedAndStampApprover()
    {
        // Arrange
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Witaj", Submitter, Created);

        // Act
        Result result = translation.Approve(Approver, Changed);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        translation.Status.ShouldBe(TranslationStatus.Approved);
        translation.ApprovedById.ShouldBe(Approver);
        translation.UpdatedAt.ShouldBe(Changed);
    }

    [Fact]
    public void Approve_WhenNeedsReview_ShouldSetApprovedAndClearInvalidation()
    {
        // Arrange — an invalidated row keeps its Polish; approving re-publishes it and resolves the
        // invalidation, so the superseded English (PreviousSourceText) is cleared (spec 0001).
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Stary polski", Submitter, Created);
        translation.ApplySourceChange(Source("New English"), ChangeVersion, Changed);
        translation.Status.ShouldBe(TranslationStatus.NeedsReview);
        translation.PreviousSourceText.ShouldBe("Old English");

        // Act
        Result result = translation.Approve(Approver, Changed);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        translation.Status.ShouldBe(TranslationStatus.Approved);
        translation.PreviousSourceText.ShouldBeNull();
        translation.ApprovedById.ShouldBe(Approver);
    }

    [Fact]
    public void Approve_WithoutTranslation_ShouldFail()
    {
        // Arrange
        Translation translation = CreateUntranslated();

        // Act
        Result result = translation.Approve(Approver, Changed);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotApproveWithoutTranslation");
        translation.Status.ShouldBe(TranslationStatus.Untranslated);
        translation.ApprovedById.ShouldBeNull();
    }

    [Fact]
    public void Approve_WhenRemoved_ShouldFail()
    {
        // Arrange
        Translation translation = CreateUntranslated();
        translation.ProvideTranslation("Polski", Submitter, Created);
        translation.MarkRemoved(ChangeVersion, Changed);

        // Act
        Result result = translation.Approve(Approver, Changed);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationEntity.CannotApproveRemoved");
    }
}
