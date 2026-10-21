using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

/// <summary>
/// The diff behind spec 0012 Tier 0. The predicate measured in E5 (#656) is "iteration moved, or the
/// subfile is gone". Everything else must stay silent, or the launch would repair for nothing.
/// </summary>
public sealed class SubFileIterationSnapshotTests
{
    private const int TextFileA = 0x25000001;
    private const int TextFileB = 0x25000002;
    private const int TextFileC = 0x25000003;
    private const int ImageFile = 0x41000001;

    [Fact]
    public void FindWipedIn_NothingMoved_ShouldBeEmpty()
    {
        // Arrange: the E5 negative control. The launcher writes the DAT on every start, and no
        // iteration moves.
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileA, 26739), (TextFileB, 26749));
        SubFileIterationSnapshot now = Snapshot((TextFileA, 26739), (TextFileB, 26749));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
        wiped.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(26739, 26751)]
    [InlineData(26751, 26739)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void FindWipedIn_IterationMovedEitherWay_ShouldReportTheSubFile(int before, int after)
    {
        // Arrange: up is a real update (49.3 → 49.4). Down is a DAT restored from an older backup.
        // Only equality counts, because the iteration is a write stamp, not a counter.
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileA, before), (TextFileB, 7));
        SubFileIterationSnapshot now = Snapshot((TextFileA, after), (TextFileB, 7));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IterationMoved.ShouldBe([TextFileA]);
        wiped.Removed.ShouldBeEmpty();
    }

    [Fact]
    public void FindWipedIn_SubFileRemoved_ShouldReportItAsRemoved()
    {
        // Arrange: 49.3 removed 378 of the subfiles 49.1 had touched (E5).
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileA, 1), (TextFileB, 1));
        SubFileIterationSnapshot now = Snapshot((TextFileA, 1));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.Removed.ShouldBe([TextFileB]);
        wiped.IterationMoved.ShouldBeEmpty();
    }

    [Fact]
    public void FindWipedIn_SubFileAddedByTheUpdate_ShouldNotReportIt()
    {
        // Arrange: a subfile that is new in the update never held a row of ours.
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileA, 1));
        SubFileIterationSnapshot now = Snapshot((TextFileA, 1), (TextFileB, 2));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FindWipedIn_IterationBumpedAndSubFileRemoved_ShouldReportBothInFileIdOrder()
    {
        // Arrange
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileC, 1), (TextFileA, 1), (TextFileB, 1));
        SubFileIterationSnapshot now = Snapshot((TextFileC, 5), (TextFileA, 2));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IterationMoved.ShouldBe([TextFileA, TextFileC]);
        wiped.Removed.ShouldBe([TextFileB]);
        wiped.Count.ShouldBe(3);
        wiped.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void FindWipedIn_SizeChangedWithTheSameIteration_ShouldNotReportTheSubFile()
    {
        // Arrange: E7 measured our own patch moving the size of 275,545 subfiles and the iteration of
        // none. A size-based check would repair after every patch of ours.
        SubFileIterationSnapshot afterPatch = SubFileIterationSnapshot.FromSubfileSizes(
            new Dictionary<int, (int Size, int Iteration)> { [TextFileA] = (100, 26739) });
        SubFileIterationSnapshot now = SubFileIterationSnapshot.FromSubfileSizes(
            new Dictionary<int, (int Size, int Iteration)> { [TextFileA] = (164, 26739) });

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FindWipedIn_NonTextSubFileReplaced_ShouldNotReportIt()
    {
        // Arrange: E7's update also replaced 35 non-text subfiles. The patcher never writes those, so
        // no row of ours can be lost there.
        SubFileIterationSnapshot afterPatch = SubFileIterationSnapshot.FromSubfileSizes(
            new Dictionary<int, (int Size, int Iteration)> { [TextFileA] = (10, 1), [ImageFile] = (5, 1) });
        SubFileIterationSnapshot now = SubFileIterationSnapshot.FromSubfileSizes(
            new Dictionary<int, (int Size, int Iteration)> { [TextFileA] = (10, 1), [ImageFile] = (5, 2) });

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FromSubfileSizes_ShouldKeepTheIterationOfEveryTextSubFileOnly()
    {
        // Act
        SubFileIterationSnapshot snapshot = SubFileIterationSnapshot.FromSubfileSizes(
            new Dictionary<int, (int Size, int Iteration)>
            {
                [TextFileA] = (10, 3),
                [TextFileB] = (20, 4),
                [ImageFile] = (5, 9)
            });

        // Assert
        snapshot.Count.ShouldBe(2);
        snapshot.Iterations[TextFileA].ShouldBe(3);
        snapshot.Iterations[TextFileB].ShouldBe(4);
        snapshot.Iterations.ContainsKey(ImageFile).ShouldBeFalse();
    }

    [Fact]
    public void FromIterations_ShouldDropNonTextSubFiles()
    {
        // Act
        SubFileIterationSnapshot snapshot = SubFileIterationSnapshot.FromIterations(
            new Dictionary<int, int> { [TextFileA] = 3, [ImageFile] = 9 });

        // Assert
        snapshot.Iterations.ShouldHaveSingleItem().Key.ShouldBe(TextFileA);
    }

    [Fact]
    public void FindWipedIn_EmptySnapshotAfterPatch_ShouldBeEmpty()
    {
        // Arrange
        SubFileIterationSnapshot afterPatch = Snapshot();
        SubFileIterationSnapshot now = Snapshot((TextFileA, 1));

        // Act
        WipedSubFileSet wiped = afterPatch.FindWipedIn(now);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FindWipedIn_NullCurrent_ShouldThrow()
    {
        // Arrange
        SubFileIterationSnapshot afterPatch = Snapshot((TextFileA, 1));

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => afterPatch.FindWipedIn(null!));
    }

    private static SubFileIterationSnapshot Snapshot(params (int FileId, int Iteration)[] subFiles) =>
        SubFileIterationSnapshot.FromIterations(
            subFiles.ToDictionary(subFile => subFile.FileId, subFile => subFile.Iteration));
}
