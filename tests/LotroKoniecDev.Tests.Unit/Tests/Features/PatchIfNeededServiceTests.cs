using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

/// <summary>
/// What the desktop app's Patch button gets from the launch's patch steps (#42): the same verdicts as
/// the launch, with the progress port passed to every patch. The launch itself is covered in
/// <see cref="GameLaunchingCommandHandlerTests"/>.
/// </summary>
public sealed class PatchIfNeededServiceTests
{
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";
    private const string VersionFilePath = @"C:\temp\version.txt";
    private const string TranslationFilePath = @"C:\translations\polish.txt";
    private const string TranslationHash = "abc123def456";
    private const string NewTranslationHash = "xyz789";

    private static readonly DatVersionInfo CurrentVnum = new(100, 200);
    private static readonly StoredVersionInfo StoredWithHash = new("40.2", 100, 200, TranslationHash);
    private static readonly WipedSubFileSet TwoWipedSubFiles = new([0x25000001], [0x25000002]);

    private readonly IGameProcessDetector _processDetector = Substitute.For<IGameProcessDetector>();
    private readonly IFileHasher _fileHasher = Substitute.For<IFileHasher>();
    private readonly IGameVersionFileStore _versionStore = Substitute.For<IGameVersionFileStore>();
    private readonly IDatVersionReader _datVersionReader = Substitute.For<IDatVersionReader>();
    private readonly IPatchingService _patchingService = Substitute.For<IPatchingService>();
    private readonly ISubFileWipeDetector _wipeDetector = Substitute.For<ISubFileWipeDetector>();
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();
    private readonly PatchIfNeededService _sut;

    [Fact]
    public void PatchIfNeeded_TranslationChanged_PatchesWithTheProgressAndSavesTheNewHash()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(NewTranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, _progress)
            .Returns(Result.Success(new PatchSummaryResponse(100, 90, 10, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, NewTranslationHash)
            .Returns(Result.Success());

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new PatchIfNeededResponse(
            TranslationChanged: true, TranslationsApplied: true, AppliedCount: 90, SkippedCount: 10, WipedSubFileCount: 0, RepairFailure: null));
        _versionStore.Received(1).SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, NewTranslationHash);
    }

    [Fact]
    public void PatchIfNeeded_TranslationUnchangedAndNothingReplaced_ChangesNothing()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _wipeDetector.Detect(DatFilePath, TranslationFilePath).Returns(WipedSubFileSet.None);

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new PatchIfNeededResponse(false, false, 0, 0, 0, null));
        _patchingService.DidNotReceive().ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>?>());
    }

    [Fact]
    public void PatchIfNeeded_TranslationUnchangedAndSubFilesReplaced_RepairsWithTheProgress()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _wipeDetector.Detect(DatFilePath, TranslationFilePath).Returns(TwoWipedSubFiles);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, _progress)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new PatchIfNeededResponse(false, true, 95, 5, 2, null));
    }

    [Fact]
    public void PatchIfNeeded_RepairFails_SucceedsWithTheReasonAndKeepsTheStoredHash()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _wipeDetector.Detect(DatFilePath, TranslationFilePath).Returns(TwoWipedSubFiles);
        Error openError = new("DatFile.CannotOpen", "CannotOpen failed", ErrorType.IoError);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, _progress)
            .Returns(Result.Failure<PatchSummaryResponse>(openError));

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new PatchIfNeededResponse(false, false, 0, 0, 2, "CannotOpen failed"));
        _versionStore.DidNotReceive().SaveVersion(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public void PatchIfNeeded_GameRunning_RefusesWithItsOwnCodeBeforeTouchingAnything()
    {
        _processDetector.IsLotroRunning().Returns(true);

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.GameLaunch.AlreadyRunningCode);
        _patchingService.DidNotReceive().ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>?>());
    }

    [Fact]
    public void PatchIfNeeded_PatchFails_ReturnsRepatchFailedWithTheReasonAndKeepsTheStoredHash()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(NewTranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        Error parseError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, _progress)
            .Returns(Result.Failure<PatchSummaryResponse>(parseError));

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.GameLaunch.RepatchFailedCode);
        result.Error.Message.ShouldBe("Applying the translations failed: Bad format");
        result.Error.Type.ShouldBe(ErrorType.Failure);
        _versionStore.DidNotReceive().SaveVersion(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public void PatchIfNeeded_NoStoredVersion_PatchesTheWholeFileWithoutTheRepairCheck()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath).Returns(Result.Success<StoredVersionInfo?>(null));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, _progress)
            .Returns(Result.Success(new PatchSummaryResponse(50, 50, 0, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, null, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, TranslationHash)
            .Returns(Result.Success());

        Result<PatchIfNeededResponse> result = _sut.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress);

        // The desktop app deletes the version file when the game file changes, and relies on this.
        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationChanged.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeTrue();
        _wipeDetector.DidNotReceive().Detect(Arg.Any<string>(), Arg.Any<string>());
    }

    public PatchIfNeededServiceTests()
    {
        _sut = new PatchIfNeededService(
            _processDetector,
            _fileHasher,
            _versionStore,
            _datVersionReader,
            _patchingService,
            _wipeDetector,
            NullLogger<PatchIfNeededService>.Instance);
    }
}
