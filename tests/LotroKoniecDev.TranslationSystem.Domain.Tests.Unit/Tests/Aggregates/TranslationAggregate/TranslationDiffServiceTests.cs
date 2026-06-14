using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationDiffServiceTests
{
    private static readonly DateTimeOffset StoredAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ImportAt = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId IntroducedVersion = GameVersionId.Create();
    private static readonly GameVersionId TargetVersion = GameVersionId.Create();

    private static Translation Existing(int fileId, long gossipId, string text, string? argsOrder = null)
        => Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(text, argsOrder, argsOrder).Value,
            IntroducedVersion,
            StoredAt).Value;

    private static IncomingSourceRow Incoming(int fileId, long gossipId, string text, string? argsOrder = null)
        => new(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(text, argsOrder, argsOrder).Value);

    [Fact]
    public void ComputePlan_OnEmptyStore_ShouldAddEveryRow()
    {
        // Arrange
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A"), Incoming(1, 2, "B")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan([], incoming, TargetVersion, ImportAt);

        // Assert
        plan.Added.Count.ShouldBe(2);
        plan.SourceChanges.ShouldBeEmpty();
        plan.Removed.ShouldBeEmpty();
        plan.UnchangedCount.ShouldBe(0);
        plan.RemovedFraction.ShouldBe(0d);
        plan.Added.ShouldAllBe(translation => translation.IntroducedInVersion == TargetVersion);
    }

    [Fact]
    public void ComputePlan_WithNewKey_ShouldAdd()
    {
        // Arrange
        Translation[] existing = [Existing(1, 1, "A")];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A"), Incoming(1, 2, "B")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.Added.Count.ShouldBe(1);
        plan.Added[0].FragmentKey.GossipId.ShouldBe(2);
        plan.UnchangedCount.ShouldBe(1);
    }

    [Fact]
    public void ComputePlan_WithIdenticalSource_ShouldBeUnchanged()
    {
        // Arrange
        Translation[] existing = [Existing(1, 1, "A")];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.UnchangedCount.ShouldBe(1);
        plan.Added.ShouldBeEmpty();
        plan.SourceChanges.ShouldBeEmpty();
        plan.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void ComputePlan_WithChangedSourceAndNoPolish_ShouldChangeSourceWithoutInvalidating()
    {
        // Arrange
        Translation[] existing = [Existing(1, 1, "Old")];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "New")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.SourceChanges.Count.ShouldBe(1);
        plan.SourceChanges[0].NewSource.Text.ShouldBe("New");
        plan.InvalidatedCount.ShouldBe(0);
    }

    [Fact]
    public void ComputePlan_WithChangedSourceAndPolish_ShouldChangeAndInvalidate()
    {
        // Arrange
        Translation withPolish = Existing(1, 1, "Old");
        withPolish.ProvideTranslation("Polski", IdentityId.Create(), StoredAt);
        IncomingSourceRow[] incoming = [Incoming(1, 1, "New")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan([withPolish], incoming, TargetVersion, ImportAt);

        // Assert
        plan.SourceChanges.Count.ShouldBe(1);
        plan.InvalidatedCount.ShouldBe(1);
    }

    [Fact]
    public void ComputePlan_WithChangedArgsOnly_ShouldBeSourceChange()
    {
        // Arrange — identical text, different argument structure is still a source change.
        Translation[] existing = [Existing(1, 1, "Text", argsOrder: "1-2")];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "Text", argsOrder: "2-1")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.SourceChanges.Count.ShouldBe(1);
        plan.UnchangedCount.ShouldBe(0);
    }

    [Fact]
    public void ComputePlan_WithExistingAbsentFromUpload_ShouldRemove()
    {
        // Arrange
        Translation[] existing = [Existing(1, 1, "A"), Existing(1, 2, "B")];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.Removed.Count.ShouldBe(1);
        plan.Removed[0].FragmentKey.GossipId.ShouldBe(2);
    }

    [Fact]
    public void ComputePlan_WithAlreadyRemovedAbsentFromUpload_ShouldNotRemoveAgain()
    {
        // Arrange
        Translation alreadyRemoved = Existing(1, 2, "B");
        alreadyRemoved.MarkRemoved(IntroducedVersion, StoredAt);
        Translation[] existing = [Existing(1, 1, "A"), alreadyRemoved];
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.Removed.ShouldBeEmpty();
        plan.ComparableExistingCount.ShouldBe(1);
    }

    [Fact]
    public void ComputePlan_WhenRemovedKeyReappearsWithIdenticalSource_ShouldRestore()
    {
        // Arrange
        Translation removed = Existing(1, 1, "A");
        removed.MarkRemoved(IntroducedVersion, StoredAt);
        IncomingSourceRow[] incoming = [Incoming(1, 1, "A")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan([removed], incoming, TargetVersion, ImportAt);

        // Assert
        plan.Restored.Count.ShouldBe(1);
        plan.SourceChanges.ShouldBeEmpty();
        plan.UnchangedCount.ShouldBe(0);
    }

    [Fact]
    public void ComputePlan_WhenRemovedKeyReappearsWithChangedSource_ShouldBeSourceChange()
    {
        // Arrange
        Translation removed = Existing(1, 1, "Old");
        removed.MarkRemoved(IntroducedVersion, StoredAt);
        IncomingSourceRow[] incoming = [Incoming(1, 1, "New")];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan([removed], incoming, TargetVersion, ImportAt);

        // Assert
        plan.SourceChanges.Count.ShouldBe(1);
        plan.Restored.ShouldBeEmpty();
    }

    [Fact]
    public void ComputePlan_RemovedFraction_ShouldBeComputedFromActiveRows()
    {
        // Arrange — five active rows, the upload keeps four and drops one.
        Translation[] existing =
        [
            Existing(1, 1, "A"), Existing(1, 2, "B"), Existing(1, 3, "C"), Existing(1, 4, "D"), Existing(1, 5, "E")
        ];
        IncomingSourceRow[] incoming =
        [
            Incoming(1, 1, "A"), Incoming(1, 2, "B"), Incoming(1, 3, "C"), Incoming(1, 4, "D")
        ];

        // Act
        TranslationDiffPlan plan = TranslationDiffService.ComputePlan(existing, incoming, TargetVersion, ImportAt);

        // Assert
        plan.Removed.Count.ShouldBe(1);
        plan.ComparableExistingCount.ShouldBe(5);
        plan.RemovedFraction.ShouldBe(0.2d);
    }
}
