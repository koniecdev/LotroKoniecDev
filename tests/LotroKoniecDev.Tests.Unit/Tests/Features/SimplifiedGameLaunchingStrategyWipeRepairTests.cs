using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Tests.Unit.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

/// <summary>
/// Spec 0012 Tier 0 end to end: the real launch strategy, wipe detector and patching service over one
/// DAT kept in memory, through a patch, a game update and the launches after it. Only the edges are
/// stand-ins: the DAT, the snapshot file, the hash, the version file and the launcher. Every launch
/// here runs with an unchanged translation file and no network, so the hash alone would never patch.
/// </summary>
public sealed class SimplifiedGameLaunchingStrategyWipeRepairTests
{
    private const string DatFilePath = "/game/client_local_English.dat";
    private const string VersionFilePath = "/data/last_known_game_version.txt";
    private const string TranslationFilePath = "/translations/polish.txt";
    private const string TranslationHash = "abc123def456";
    private const string English = "Test";
    private const string Polish = "Polski tekst";
    private const ulong FragmentId = 1;
    private const int TextFileA = 0x25000001;
    private const int TextFileB = 0x25000002;
    private const int TextFileC = 0x25000003;
    private const int IterationAfterPatch = 26739;
    private const int IterationAfterUpdate = 26751;

    private static readonly string EnglishDigest = SourceDigest.ForExportForm(English, 0);

    private readonly InMemoryDatFileHandler _dat = new();
    private readonly InMemorySnapshotStore _snapshotStore = new();
    private readonly ITranslationParser _translationParser;
    private readonly PatchingService _patchingService;
    private readonly SimplifiedGameLaunchingStrategy _sut;

    [Fact]
    public async Task ExecuteAsync_AfterAnUpdateReplacedOnePatchedSubFileAndRemovedAnother_ShouldDetectBothAndRestoreTheRows()
    {
        // Arrange: the update puts A back with the same English and a new iteration (a collateral
        // revert), and removes B. C is untouched and keeps its Polish.
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);
        _dat.RemoveSubFile(TextFileB);

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.WipedSubFileCount.ShouldBe(2);
        result.Value.TranslationsApplied.ShouldBeTrue();
        _dat.TextOf(TextFileA, FragmentId).ShouldBe(Polish);
        _dat.TextOf(TextFileC, FragmentId).ShouldBe(Polish);
    }

    [Fact]
    public async Task ExecuteAsync_AfterAnUpdateThatChangedTheEnglishOfARow_ShouldLeaveThatRowInEnglishAndRepairTheRest()
    {
        // Arrange: the repair writes through the source guard (ADR-0047), so the translation of
        // English that SSG changed must not come back.
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, "Changed by SSG", IterationAfterUpdate);
        _dat.ReplaceTextSubFile(TextFileB, English, IterationAfterUpdate);

        // Act
        await Launch();

        // Assert
        _dat.TextOf(TextFileA, FragmentId).ShouldBe("Changed by SSG");
        _dat.TextOf(TextFileB, FragmentId).ShouldBe(Polish);
    }

    [Fact]
    public async Task ExecuteAsync_RightAfterARepair_ShouldFindNothingAndWriteNothing()
    {
        // Arrange: the repair saved a new snapshot, which is what stops it from running again.
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);
        await Launch();
        int writesAfterRepair = _dat.WriteCount;

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.Value.WipedSubFileCount.ShouldBe(0);
        result.Value.TranslationsApplied.ShouldBeFalse();
        _dat.WriteCount.ShouldBe(writesAfterRepair);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoUpdateSinceThePatch_ShouldNotTouchTheDat()
    {
        // Arrange: the E5 negative control. Nothing moved the iterations after our patch.
        Patch();
        int writesAfterPatch = _dat.WriteCount;

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.WipedSubFileCount.ShouldBe(0);
        result.Value.TranslationsApplied.ShouldBeFalse();
        _dat.WriteCount.ShouldBe(writesAfterPatch);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheRepairCannotOpenTheDatForWriting_ShouldStillStartTheGame()
    {
        // Arrange: a launch without elevation. Before Tier 0 it started the game, so it still must.
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);
        _dat.RefuseWriteAccess = true;

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeFalse();
        result.Value.WipedSubFileCount.ShouldBe(1);
        result.Value.RepairFailure.ShouldNotBeNull();
        _dat.TextOf(TextFileA, FragmentId).ShouldBe(English);
    }

    [Fact]
    public async Task ExecuteAsync_AfterARepairThatFailed_ShouldTryAgain()
    {
        // Arrange: the failed repair saved no snapshot, so the old one still shows the wiped set.
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);
        _dat.RefuseWriteAccess = true;
        await Launch();
        _dat.RefuseWriteAccess = false;

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.Value.TranslationsApplied.ShouldBeTrue();
        _dat.TextOf(TextFileA, FragmentId).ShouldBe(Polish);
    }

    [Fact]
    public async Task ExecuteAsync_OnAnInstallPatchedBeforeSnapshotsExisted_ShouldBehaveAsBefore()
    {
        // Arrange: an older patcher wrote the DAT and saved no snapshot, and an update has replaced A
        // since then. Nothing tells us what our patch left, so nothing may be forced.
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.WipedSubFileCount.ShouldBe(0);
        result.Value.TranslationsApplied.ShouldBeFalse();
        _dat.WriteCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithADamagedSnapshot_ShouldNeverForceARepair()
    {
        // Arrange
        Patch();
        _dat.ReplaceTextSubFile(TextFileA, English, IterationAfterUpdate);
        _snapshotStore.IsDamaged = true;
        int writesAfterPatch = _dat.WriteCount;

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.WipedSubFileCount.ShouldBe(0);
        _dat.WriteCount.ShouldBe(writesAfterPatch);
    }

    [Fact]
    public async Task ExecuteAsync_WithRowsThePatcherSkips_ShouldDetectNothingWithoutAnUpdate()
    {
        // Arrange: every row class the patch skips (#565, owner comment of 2026-08-16): not approved,
        // a subfile the DAT does not hold, a non-text subfile, a piece that is too long and a fragment
        // that is not there. They are in the artifact and never in the DAT, and the snapshot knows
        // nothing of rows, so none of them may look like a revert.
        const int ImageFile = 0x41000001;
        _dat.AddNonTextSubFile(ImageFile, IterationAfterPatch);
        GivenTranslationFile(
            Row(TextFileA, isApproved: false),
            Row(0x25FFFFFF),
            Row(ImageFile),
            Row(TextFileB, content: new string('x', DatFileConstants.MaxTextPieceLength + 1)),
            Row(TextFileC, gossipId: 99));
        Patch();

        // Act
        Result<GameLaunchingResponse> result = await Launch();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.WipedSubFileCount.ShouldBe(0);
        _dat.WriteCount.ShouldBe(0);
    }

    public SimplifiedGameLaunchingStrategyWipeRepairTests()
    {
        _translationParser = Substitute.For<ITranslationParser>();

        ITranslationLedger ledger = Substitute.For<ITranslationLedger>();
        ledger.Read(Arg.Any<string>()).Returns(new Dictionary<LedgerKey, string>());
        ledger.Save(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<LedgerKey, string>>()).Returns(Result.Success());

        _patchingService = new PatchingService(_dat, _translationParser, ledger, _snapshotStore);

        IFileHasher fileHasher = Substitute.For<IFileHasher>();
        fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));

        IGameVersionFileStore versionStore = Substitute.For<IGameVersionFileStore>();
        versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(new StoredVersionInfo(null, 100, 200, TranslationHash)));

        IGameLauncher launcher = Substitute.For<IGameLauncher>();
        launcher.Launch(DatFilePath).Returns(Result.Success());

        _sut = new SimplifiedGameLaunchingStrategy(
            Substitute.For<IGameProcessDetector>(),
            fileHasher,
            versionStore,
            Substitute.For<IDatVersionReader>(),
            _patchingService,
            new SubFileWipeDetector(_dat, _snapshotStore, NullLogger<SubFileWipeDetector>.Instance),
            launcher,
            NullLogger<SimplifiedGameLaunchingStrategy>.Instance);

        _dat.AddTextSubFile(TextFileA, English, IterationAfterPatch);
        _dat.AddTextSubFile(TextFileB, English, IterationAfterPatch);
        _dat.AddTextSubFile(TextFileC, English, IterationAfterPatch);
        GivenTranslationFile(Row(TextFileA), Row(TextFileB), Row(TextFileC));
    }

    /// <summary>The <c>patch</c> command, which goes through the same service as every launch.</summary>
    private void Patch() => _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath);

    private ValueTask<Result<GameLaunchingResponse>> Launch() =>
        _sut.ExecuteAsync(
            new GameLaunchingCommand(DatFilePath, VersionFilePath, TranslationFilePath),
            CancellationToken.None);

    private void GivenTranslationFile(params Translation[] rows) =>
        _translationParser.ParseFile(TranslationFilePath)
            .Returns(Result.Success(new TranslationParseResult(rows, [], 0)));

    private static Translation Row(
        int fileId,
        ulong gossipId = FragmentId,
        string content = Polish,
        bool isApproved = true) =>
        new()
        {
            FileId = fileId,
            GossipId = gossipId,
            Content = content,
            IsApproved = isApproved,
            SourceDigest = EnglishDigest
        };

    /// <summary>The snapshot file next to the translation file, kept in memory across launches.</summary>
    private sealed class InMemorySnapshotStore : ISubFileIterationSnapshotStore
    {
        private SubFileIterationSnapshot? _snapshot;

        public bool IsDamaged { get; set; }

        public Result<SubFileIterationSnapshot?> Read(string translationFilePath) =>
            IsDamaged
                ? Result.Failure<SubFileIterationSnapshot?>(
                    DomainErrors.IterationSnapshot.ReadError(translationFilePath, "line 1 is broken"))
                : Result.Success<SubFileIterationSnapshot?>(_snapshot);

        public Result Save(string translationFilePath, SubFileIterationSnapshot snapshot)
        {
            _snapshot = snapshot;
            IsDamaged = false;

            return Result.Success();
        }
    }
}
