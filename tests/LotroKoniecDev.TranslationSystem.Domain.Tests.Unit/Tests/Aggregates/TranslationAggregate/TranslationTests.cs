using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationTests
{
    private static readonly DateTimeOffset Created = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Changed = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId IntroducedVersion = GameVersionId.Create();
    private static readonly GameVersionId ChangeVersion = GameVersionId.Create();

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
        translation.ProvideTranslation("Stary polski", Created);

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
        translation.ProvideTranslation("Polski", Created);
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
        translation.ProvideTranslation("Witaj", Changed);

        // Assert
        translation.Status.ShouldBe(TranslationStatus.Draft);
        translation.TranslatedText.ShouldBe("Witaj");
        translation.UpdatedAt.ShouldBe(Changed);
    }
}
