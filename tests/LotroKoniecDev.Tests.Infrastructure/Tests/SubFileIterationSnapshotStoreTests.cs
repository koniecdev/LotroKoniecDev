using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Infrastructure.Storage;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the iteration snapshot of spec 0012 Tier 0 against real files. The whole property is: a
/// saved snapshot reads back exactly, and anything that is not exactly what we wrote reads as "no
/// usable snapshot", never as a different one, because a wrong snapshot forces a repair.
/// </summary>
public sealed class SubFileIterationSnapshotStoreTests : IDisposable
{
    private const int TextFileA = 0x25000001;
    private const int TextFileB = 0x25000002;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lotro-iterations-tests-{Guid.NewGuid():N}");

    private readonly SubFileIterationSnapshotStore _sut = new();

    private string TranslationFilePath => Path.Combine(_directory, "translations", "polish.txt");

    private string SnapshotPath => TranslationFilePath + ".iterations";

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Read_WithNoSnapshotOnDisk_ShouldReturnNull()
    {
        // Act: the first run, or an install patched before snapshots existed.
        Result<SubFileIterationSnapshot?> result = _sut.Read(TranslationFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public void Save_ThenRead_ShouldRoundTripEveryIteration()
    {
        // Arrange: the column is a signed int from the DAT, so both ends and zero have to come back.
        SubFileIterationSnapshot snapshot = Snapshot(
            (TextFileA, 26751), (TextFileB, int.MaxValue), (0x25000003, 0), (0x25000004, int.MinValue));

        // Act
        Result saveResult = _sut.Save(TranslationFilePath, snapshot);
        Result<SubFileIterationSnapshot?> read = _sut.Read(TranslationFilePath);

        // Assert
        saveResult.IsSuccess.ShouldBeTrue();
        read.Value.ShouldNotBeNull();
        read.Value.Count.ShouldBe(4);
        read.Value.Iterations[TextFileA].ShouldBe(26751);
        read.Value.Iterations[TextFileB].ShouldBe(int.MaxValue);
        read.Value.Iterations[0x25000003].ShouldBe(0);
        read.Value.Iterations[0x25000004].ShouldBe(int.MinValue);
    }

    [Fact]
    public void Save_ShouldWriteOneLinePerSubFileInFileIdOrder()
    {
        // Act
        _sut.Save(TranslationFilePath, Snapshot((TextFileB, 2), (TextFileA, 1)));

        // Assert
        File.ReadAllLines(SnapshotPath).ShouldBe([$"{TextFileA}||1", $"{TextFileB}||2"]);
    }

    [Fact]
    public void Save_ShouldLandNextToTheTranslationFileLikeTheLedger()
    {
        // Act
        _sut.Save(TranslationFilePath, Snapshot((TextFileA, 1)));

        // Assert
        File.Exists(SnapshotPath).ShouldBeTrue();
    }

    [Fact]
    public void Save_Twice_ShouldKeepOnlyTheLatestSnapshot()
    {
        // Arrange: a repair replaces the snapshot of the patch before it.
        _sut.Save(TranslationFilePath, Snapshot((TextFileA, 1), (TextFileB, 1)));

        // Act
        _sut.Save(TranslationFilePath, Snapshot((TextFileA, 2)));

        // Assert
        _sut.Read(TranslationFilePath).Value!.Iterations.ShouldHaveSingleItem()
            .ShouldBe(new KeyValuePair<int, int>(TextFileA, 2));
    }

    [Fact]
    public void Save_ShouldNotLeaveItsTemporaryFileBehind()
    {
        // Act
        _sut.Save(TranslationFilePath, Snapshot((TextFileA, 1)));

        // Assert
        File.Exists(SnapshotPath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Save_WithNoSubFiles_ShouldReadBackAsAnEmptySnapshot()
    {
        // Act
        Result saveResult = _sut.Save(TranslationFilePath, Snapshot());

        // Assert
        saveResult.IsSuccess.ShouldBeTrue();
        _sut.Read(TranslationFilePath).Value!.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("620756993")]
    [InlineData("620756993||")]
    [InlineData("||7")]
    [InlineData("notanumber||7")]
    [InlineData("620756993||notanumber")]
    [InlineData("33554433||7")]
    [InlineData("-620756993||7")]
    [InlineData("620756993|| 7")]
    [InlineData("620756993||7||8")]
    [InlineData("620756993||99999999999")]
    [InlineData("620756993||٧")]
    [InlineData("620756994||8")]
    public void Read_WithALineThatIsNotOursOrRepeatsAFileId_ShouldFailInsteadOfGuessing(string badLine)
    {
        // Arrange: 33554433 is a non-text subfile, which the patch never saves. The last case repeats
        // the file id of the first line. Unlike the ledger, one bad line makes the whole file unusable:
        // if the file is not what we wrote, its good-looking lines are not trustworthy either, and
        // trusting them could force a repair that nothing justifies (spec 0012 asks for none).
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllLines(SnapshotPath, [$"{TextFileB}||7", badLine, $"{TextFileA}||1"]);

        // Act
        Result<SubFileIterationSnapshot?> result = _sut.Read(TranslationFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("IterationSnapshot.ReadError");
    }

    [Fact]
    public void Read_WhileAnotherProcessHoldsTheFile_ShouldFailWithoutThrowing()
    {
        // Arrange
        _sut.Save(TranslationFilePath, Snapshot((TextFileA, 1)));
        using FileStream locked = new(SnapshotPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        Result<SubFileIterationSnapshot?> result = _sut.Read(TranslationFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("IterationSnapshot.ReadError");
    }

    [Fact]
    public void Save_WhenTheSnapshotPathIsADirectory_ShouldFailWithoutThrowing()
    {
        // Arrange
        Directory.CreateDirectory(SnapshotPath);

        // Act
        Result result = _sut.Save(TranslationFilePath, Snapshot((TextFileA, 1)));

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("IterationSnapshot.WriteError");
    }

    private static SubFileIterationSnapshot Snapshot(params (int FileId, int Iteration)[] subFiles) =>
        SubFileIterationSnapshot.FromIterations(
            subFiles.ToDictionary(subFile => subFile.FileId, subFile => subFile.Iteration));
}
