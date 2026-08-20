using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class TranslationDiffServiceTests
{
    private static SourceHash Hash(string text, string? argsOrder = null)
        => SourceHash.Compute(TranslationSource.Create(text, argsOrder, argsOrder).Value);

    private static StoredSourceDigest Stored(
        int fileId,
        long gossipId,
        string text,
        string? argsOrder = null,
        TranslationStatus status = TranslationStatus.Untranslated,
        bool isRemoved = false,
        string? polish = null)
        => new(
            TranslationId.Create(),
            new FragmentKeyValue(fileId, gossipId),
            Hash(text, argsOrder),
            SourceHash.ComputeEcho(polish, argsOrder, argsOrder),
            status,
            isRemoved);

    private static KeyValuePair<FragmentKeyValue, SourceHash> Incoming(
        int fileId,
        long gossipId,
        string text,
        string? argsOrder = null)
        => new(new FragmentKeyValue(fileId, gossipId), Hash(text, argsOrder));

    private static Dictionary<FragmentKeyValue, SourceHash> Map(params KeyValuePair<FragmentKeyValue, SourceHash>[] rows)
        => new(rows);

    private static async IAsyncEnumerable<StoredSourceDigest> Existing(params StoredSourceDigest[] digests)
    {
        await Task.Yield();
        foreach (StoredSourceDigest digest in digests)
        {
            yield return digest;
        }
    }

    [Fact]
    public async Task ComputePlanAsync_OnEmptyStore_ShouldAddEveryRow()
    {
        // Arrange
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"), Incoming(1, 2, "B"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(), incoming, CancellationToken.None);

        // Assert
        plan.AddedCount.ShouldBe(2);
        plan.IsAdded(new FragmentKeyValue(1, 1)).ShouldBeTrue();
        plan.IsAdded(new FragmentKeyValue(1, 2)).ShouldBeTrue();
        plan.SourceChangedByKey.ShouldBeEmpty();
        plan.RemovedIds.ShouldBeEmpty();
        plan.UnchangedCount.ShouldBe(0);
        plan.RemovedFraction.ShouldBe(0d);
    }

    [Fact]
    public async Task ComputePlanAsync_WithNewKey_ShouldAdd()
    {
        // Arrange
        StoredSourceDigest[] existing = [Stored(1, 1, "A")];
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"), Incoming(1, 2, "B"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(existing), incoming, CancellationToken.None);

        // Assert
        plan.AddedCount.ShouldBe(1);
        plan.IsAdded(new FragmentKeyValue(1, 2)).ShouldBeTrue();
        plan.IsAdded(new FragmentKeyValue(1, 1)).ShouldBeFalse();
        plan.UnchangedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ComputePlanAsync_WithIdenticalSource_ShouldBeUnchanged()
    {
        // Arrange
        StoredSourceDigest[] existing = [Stored(1, 1, "A")];
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(existing), incoming, CancellationToken.None);

        // Assert
        plan.UnchangedCount.ShouldBe(1);
        plan.AddedCount.ShouldBe(0);
        plan.SourceChangedByKey.ShouldBeEmpty();
        plan.RemovedIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputePlanAsync_WithChangedSourceAndNoPolish_ShouldChangeSourceWithoutInvalidating()
    {
        // Arrange
        StoredSourceDigest stored = Stored(1, 1, "Old");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "New"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.SourceChangedByKey[new FragmentKeyValue(1, 1)].ShouldBe(stored.Id);
        plan.InvalidatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(TranslationStatus.Draft)]
    [InlineData(TranslationStatus.Approved)]
    [InlineData(TranslationStatus.NeedsReview)]
    public async Task ComputePlanAsync_WithChangedSourceAndPolishWork_ShouldChangeAndInvalidate(TranslationStatus status)
    {
        // Arrange: any status carrying Polish work counts as invalidated by a source change.
        StoredSourceDigest stored = Stored(1, 1, "Old", status: status);
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "New"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.InvalidatedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ComputePlanAsync_WithChangedArgsOnly_ShouldBeSourceChange()
    {
        // Arrange: identical text, different argument structure is still a source change.
        StoredSourceDigest[] existing = [Stored(1, 1, "Text", argsOrder: "1-2")];
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Text", argsOrder: "2-1"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(existing), incoming, CancellationToken.None);

        // Assert
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.UnchangedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ComputePlanAsync_WithExistingAbsentFromUpload_ShouldRemove()
    {
        // Arrange
        StoredSourceDigest kept = Stored(1, 1, "A");
        StoredSourceDigest dropped = Stored(1, 2, "B");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(kept, dropped), incoming, CancellationToken.None);

        // Assert
        plan.RemovedIds.Count.ShouldBe(1);
        plan.RemovedIds[0].ShouldBe(dropped.Id);
    }

    [Fact]
    public async Task ComputePlanAsync_WithAlreadyRemovedAbsentFromUpload_ShouldNotRemoveAgain()
    {
        // Arrange
        StoredSourceDigest active = Stored(1, 1, "A");
        StoredSourceDigest alreadyRemoved = Stored(1, 2, "B", isRemoved: true);
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(active, alreadyRemoved), incoming, CancellationToken.None);

        // Assert
        plan.RemovedIds.ShouldBeEmpty();
        plan.ComparableExistingCount.ShouldBe(1);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenRemovedKeyReappearsWithIdenticalSource_ShouldRestore()
    {
        // Arrange
        StoredSourceDigest removed = Stored(1, 1, "A", isRemoved: true);
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(removed), incoming, CancellationToken.None);

        // Assert
        plan.RestoredIds.Count.ShouldBe(1);
        plan.RestoredIds[0].ShouldBe(removed.Id);
        plan.SourceChangedByKey.ShouldBeEmpty();
        plan.UnchangedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenRemovedKeyReappearsWithChangedSource_ShouldBeSourceChange()
    {
        // Arrange
        StoredSourceDigest removed = Stored(1, 1, "Old", isRemoved: true);
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "New"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(removed), incoming, CancellationToken.None);

        // Assert
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.RestoredIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputePlanAsync_RemovedFraction_ShouldBeComputedFromActiveRows()
    {
        // Arrange: five active rows, the upload keeps four and drops one.
        StoredSourceDigest[] existing =
        [
            Stored(1, 1, "A"), Stored(1, 2, "B"), Stored(1, 3, "C"), Stored(1, 4, "D"), Stored(1, 5, "E")
        ];
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(
            Incoming(1, 1, "A"), Incoming(1, 2, "B"), Incoming(1, 3, "C"), Incoming(1, 4, "D"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(existing), incoming, CancellationToken.None);

        // Assert
        plan.RemovedIds.Count.ShouldBe(1);
        plan.ComparableExistingCount.ShouldBe(5);
        plan.RemovedFraction.ShouldBe(0.2d);
    }

    [Fact]
    public async Task ComputePlanAsync_ShouldConsumeTheIncomingMapDownToTheAddedKeys()
    {
        // Arrange: one matched key, one new key: the diff owns the map and reduces it to the
        // added set the apply pass filters the re-streamed upload against.
        StoredSourceDigest stored = Stored(1, 1, "A");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "A"), Incoming(1, 9, "New"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        incoming.Count.ShouldBe(1);
        incoming.ContainsKey(new FragmentKeyValue(1, 9)).ShouldBeTrue();
        plan.AddedCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(TranslationStatus.Draft)]
    [InlineData(TranslationStatus.Approved)]
    [InlineData(TranslationStatus.NeedsReview)]
    public async Task ComputePlanAsync_WhenIncomingSourceIsTheRowsOwnPolish_ShouldBeUnchangedEcho(TranslationStatus status)
    {
        // Arrange: the export came from a patched DAT, so the resident row carries our Polish as
        // its "source" (spec 0012); whatever the status, the current Polish is what would echo.
        StoredSourceDigest stored = Stored(1, 1, "Alpha", status: status, polish: "Alfa");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(1);
        plan.UnchangedCount.ShouldBe(1);
        plan.SourceChangedByKey.ShouldBeEmpty();
        plan.InvalidatedCount.ShouldBe(0);
        plan.RestoredIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputePlanAsync_WhenIncomingSourceIsSomeoneElsesPolish_ShouldBeSourceChange()
    {
        // Arrange: only the row's own Polish counts as our text coming back. A different Polish text is a
        // real change, for example an older Polish still sitting in the DAT after a re-edit, which the
        // guard cannot recognise.
        StoredSourceDigest stored = Stored(1, 1, "Alpha", status: TranslationStatus.Approved, polish: "Alfa");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa stara"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(0);
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.InvalidatedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenRowHasNoPolish_ShouldNeverEcho()
    {
        // Arrange: an untranslated row has nothing of ours that could come back; any differing text
        // is a source change even if it happens to look Polish.
        StoredSourceDigest stored = Stored(1, 1, "Alpha");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(0);
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.InvalidatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenPolishEchoesWithIdenticalArgs_ShouldBeUnchangedEcho()
    {
        // Arrange: a placeholder-bearing row: the patched DAT keeps the argument count, so the
        // export re-emits the source's identity args next to our Polish. Text AND args match the
        // echo triple.
        StoredSourceDigest stored = Stored(1, 1, "Hail <--DO_NOT_TOUCH!--> friend", argsOrder: "1-1", status: TranslationStatus.Approved, polish: "Witaj <--DO_NOT_TOUCH!--> przyjacielu");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Witaj <--DO_NOT_TOUCH!--> przyjacielu", argsOrder: "1-1"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(1);
        plan.UnchangedCount.ShouldBe(1);
        plan.SourceChangedByKey.ShouldBeEmpty();
        plan.InvalidatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenPolishEchoesWithDifferentArgs_ShouldBeSourceChange()
    {
        // Arrange: the echo is the whole triple: our Polish text with the source's args columns. A
        // changed argument structure is a real change even when the text is our own Polish.
        StoredSourceDigest stored = Stored(1, 1, "Alpha", argsOrder: "1-2", status: TranslationStatus.Approved, polish: "Alfa");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa", argsOrder: "1-2-3"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(0);
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.InvalidatedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ComputePlanAsync_WhenStoredSourceAlreadyEqualsThePolish_ShouldBePlainUnchangedNotEcho()
    {
        // Arrange: a poisoned row, whose source an import before the guard overwrote with the Polish. The
        // source check wins, so the row counts as unchanged and not as our text coming back. That counter
        // only reports rows the guard caught.
        StoredSourceDigest stored = Stored(1, 1, "Alfa", status: TranslationStatus.Approved, polish: "Alfa");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(stored), incoming, CancellationToken.None);

        // Assert
        plan.UnchangedCount.ShouldBe(1);
        plan.EchoedCount.ShouldBe(0);
        plan.SourceChangedByKey.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputePlanAsync_WhenRemovedRowReappearsAsItsOwnPolish_ShouldRestoreAndCountEcho()
    {
        // Arrange: an echo proves the fragment still holds our patch, i.e. its source never really
        // changed, so a soft-removed row follows the identical-source re-add rule and is restored.
        StoredSourceDigest removed = Stored(1, 1, "Alpha", status: TranslationStatus.Approved, isRemoved: true, polish: "Alfa");
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(Incoming(1, 1, "Alfa"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(removed), incoming, CancellationToken.None);

        // Assert
        plan.RestoredIds.Count.ShouldBe(1);
        plan.RestoredIds[0].ShouldBe(removed.Id);
        plan.EchoedCount.ShouldBe(1);
        plan.UnchangedCount.ShouldBe(0);
        plan.SourceChangedByKey.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputePlanAsync_OnPatchedDatExport_ShouldInvalidateOnlyTheRealEnglishChange()
    {
        // Arrange: the U49 shape (spec 0012 AC): a translated corpus, an export from the admin's
        // patched DAT. Rows 1-2 echo our Polish, row 3 was collateral-reverted to its identical
        // English (a client-side repair, not a TMS event), row 4's English really changed, row 5 is
        // untranslated and untouched.
        StoredSourceDigest[] existing =
        [
            Stored(1, 1, "Alpha", status: TranslationStatus.Approved, polish: "Alfa"),
            Stored(1, 2, "Beta", status: TranslationStatus.Approved, polish: "Bety"),
            Stored(1, 3, "Gamma", status: TranslationStatus.Approved, polish: "Gama"),
            Stored(1, 4, "Delta", status: TranslationStatus.Approved, polish: "Delty"),
            Stored(1, 5, "Epsilon")
        ];
        Dictionary<FragmentKeyValue, SourceHash> incoming = Map(
            Incoming(1, 1, "Alfa"),
            Incoming(1, 2, "Bety"),
            Incoming(1, 3, "Gamma"),
            Incoming(1, 4, "Delta reworded"),
            Incoming(1, 5, "Epsilon"));

        // Act
        TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(Existing(existing), incoming, CancellationToken.None);

        // Assert
        plan.EchoedCount.ShouldBe(2);
        plan.UnchangedCount.ShouldBe(4);
        plan.SourceChangedByKey.Count.ShouldBe(1);
        plan.SourceChangedByKey.ContainsKey(new FragmentKeyValue(1, 4)).ShouldBeTrue();
        plan.InvalidatedCount.ShouldBe(1);
        plan.AddedCount.ShouldBe(0);
        plan.RemovedIds.ShouldBeEmpty();
    }
}
