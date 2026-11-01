using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class SubFileWipeDetectorTests
{
    private const string DatFilePath = "/game/client_local_English.dat";
    private const string TranslationFilePath = "/translations/polish.txt";
    private const int ReadOnlyHandle = 7;
    private const int TextFileA = 0x25000001;
    private const int TextFileB = 0x25000002;
    private const int TextFileC = 0x25000003;
    private const int ImageFile = 0x41000001;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ISubFileIterationSnapshotStore _snapshotStore;
    private readonly SubFileWipeDetector _sut;

    [Fact]
    public void Detect_IterationBumpedAndSubFileRemoved_ShouldReturnBoth()
    {
        // Arrange: spec 0012 criterion 1, the write-test DAT of the ticket.
        GivenSnapshotAfterPatch((TextFileA, 1), (TextFileB, 1), (TextFileC, 1));
        GivenDatNow((TextFileA, 2), (TextFileC, 1));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IterationMoved.ShouldBe([TextFileA]);
        wiped.Removed.ShouldBe([TextFileB]);
    }

    [Fact]
    public void Detect_NothingMoved_ShouldReturnAnEmptySet()
    {
        // Arrange
        GivenSnapshotAfterPatch((TextFileA, 1), (TextFileB, 1));
        GivenDatNow((TextFileA, 1), (TextFileB, 1));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Detect_OnlyANonTextSubFileMoved_ShouldReturnAnEmptySet()
    {
        // Arrange
        GivenSnapshotAfterPatch((TextFileA, 1));
        GivenDatNow((TextFileA, 1), (ImageFile, 9));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Detect_WithNoSnapshotSavedYet_ShouldReturnAnEmptySet()
    {
        // Arrange: the first run, or an install patched before snapshots existed.
        _snapshotStore.Read(TranslationFilePath).Returns(Result.Success<SubFileIterationSnapshot?>(null));
        GivenDatNow((TextFileA, 2));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Detect_WithAnUnreadableSnapshot_ShouldReturnAnEmptySet()
    {
        // Arrange
        _snapshotStore.Read(TranslationFilePath).Returns(Result.Failure<SubFileIterationSnapshot?>(
            DomainErrors.IterationSnapshot.ReadError(TranslationFilePath, "line 3 is broken")));
        GivenDatNow((TextFileA, 2));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Detect_WithAnEmptySnapshot_ShouldReturnAnEmptySet()
    {
        // Arrange: the patch never saves an empty snapshot, so this is a damaged file. It gets its
        // own warning, so it does not read as "all is well".
        GivenSnapshotAfterPatch();
        GivenDatNow((TextFileA, 2));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert: an empty set comes back either way, so the proof is that nothing was compared.
        wiped.IsEmpty.ShouldBeTrue();
        _datFileHandler.DidNotReceive().Open(Arg.Any<string>(), Arg.Any<DatFileAccess>());
    }

    [Fact]
    public void Detect_WhenTheDatCannotBeOpened_ShouldReturnAnEmptySet()
    {
        // Arrange
        GivenSnapshotAfterPatch((TextFileA, 1));
        _datFileHandler.Open(DatFilePath, DatFileAccess.Read)
            .Returns(Result.Failure<int>(DomainErrors.DatFile.CannotOpen(DatFilePath)));

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detect_WhenTheDatListsNoTextSubFile_ShouldReturnAnEmptySet(bool withNonTextSubFiles)
    {
        // Arrange: read at face value, an empty list would report every subfile as removed.
        GivenSnapshotAfterPatch((TextFileA, 1), (TextFileB, 1));
        GivenDatNow(withNonTextSubFiles ? [(ImageFile, 1)] : []);

        // Act
        WipedSubFileSet wiped = _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        wiped.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Detect_ShouldCloseTheDatItOpened()
    {
        // Arrange: the patch that may follow opens the same DAT for writing.
        GivenSnapshotAfterPatch((TextFileA, 1));
        GivenDatNow((TextFileA, 2));

        // Act
        _sut.Detect(DatFilePath, TranslationFilePath);

        // Assert
        _datFileHandler.Received(1).Close(ReadOnlyHandle);
    }

    [Fact]
    public void Detect_WhenReadingTheIterationsThrows_ShouldStillCloseTheDat()
    {
        // Arrange
        GivenSnapshotAfterPatch((TextFileA, 1));
        _datFileHandler.GetAllSubfileSizes(ReadOnlyHandle).Returns(_ => throw new InvalidOperationException("native fault"));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => _sut.Detect(DatFilePath, TranslationFilePath));
        _datFileHandler.Received(1).Close(ReadOnlyHandle);
    }

    public SubFileWipeDetectorTests()
    {
        _datFileHandler = Substitute.For<IDatFileHandler>();

        // Detection never asks for write access: the read-only open needs no elevation (#629). A
        // read-write open fails here, so every test that finds a wipe also proves the open was read-only.
        _datFileHandler.Open(DatFilePath, DatFileAccess.Read).Returns(Result.Success(ReadOnlyHandle));
        _datFileHandler.Open(DatFilePath, DatFileAccess.ReadWrite)
            .Returns(Result.Failure<int>(DomainErrors.DatFile.CannotOpen(DatFilePath)));

        _snapshotStore = Substitute.For<ISubFileIterationSnapshotStore>();

        _sut = new SubFileWipeDetector(_datFileHandler, _snapshotStore, NullLogger<SubFileWipeDetector>.Instance);
    }

    private void GivenSnapshotAfterPatch(params (int FileId, int Iteration)[] subFiles) =>
        _snapshotStore.Read(TranslationFilePath).Returns(Result.Success<SubFileIterationSnapshot?>(
            SubFileIterationSnapshot.FromIterations(
                subFiles.ToDictionary(subFile => subFile.FileId, subFile => subFile.Iteration))));

    private void GivenDatNow(params (int FileId, int Iteration)[] subFiles) =>
        _datFileHandler.GetAllSubfileSizes(ReadOnlyHandle).Returns(
            subFiles.ToDictionary(subFile => subFile.FileId, subFile => (100, subFile.Iteration)));
}
