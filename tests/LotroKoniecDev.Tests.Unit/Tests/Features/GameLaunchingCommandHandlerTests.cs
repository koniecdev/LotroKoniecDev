using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class GameLaunchingCommandHandlerTests
{
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";
    private const string VersionFilePath = @"C:\temp\version.txt";
    private const string TranslationFilePath = @"C:\translations\polish.txt";
    private const int GameExitCode = 0;
    private const string ForumVersion = "40.2";

    private static readonly DatVersionInfo CurrentVnum = new(100, 200);
    private static readonly DatVersionInfo UpdatedVnum = new(100, 201);
    // Stored with same forum version + same vnum → no update, normal launch
    private static readonly StoredVersionInfo StoredCurrent = new(ForumVersion, 100, 200);
    // Stored with old forum version + same vnum → forum says update, vnum unchanged → forced launcher
    private static readonly StoredVersionInfo StoredOldForum = new("40.1", 100, 200);
    // Stored with old forum version + old vnum → vnum changed → re-patch without forced launcher
    private static readonly StoredVersionInfo StoredOldVnum = new("40.1", 100, 199);

    private readonly IGameUpdateChecker _updateChecker;
    private readonly IGameVersionFileStore _versionStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IDatFileProtector _protector;
    private readonly IGameLauncher _launcher;
    private readonly IGameProcessDetector _processDetector;
    private readonly IPatchingService _patchingService;
    private readonly GameLaunchingCommandHandler _sut;

    public GameLaunchingCommandHandlerTests()
    {
        _updateChecker = Substitute.For<IGameUpdateChecker>();
        _versionStore = Substitute.For<IGameVersionFileStore>();
        _datVersionReader = Substitute.For<IDatVersionReader>();
        _protector = Substitute.For<IDatFileProtector>();
        _launcher = Substitute.For<IGameLauncher>();
        _processDetector = Substitute.For<IGameProcessDetector>();
        _patchingService = Substitute.For<IPatchingService>();

        _sut = new GameLaunchingCommandHandler(
            _updateChecker,
            _versionStore,
            _datVersionReader,
            _protector,
            _launcher,
            _processDetector,
            _patchingService,
            NullLogger<GameLaunchingCommandHandler>.Instance);
    }

    private static GameLaunchingCommand CreateCommand() =>
        new(DatFilePath, VersionFilePath, TranslationFilePath);

    private void SetupNoUpdate()
    {
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredCurrent)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
    }

    private void SetupVnumChanged()
    {
        // Forum says same version, but DAT vnum changed (game updated outside patcher)
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldVnum)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
    }

    private void SetupForumUpdateVnumUnchanged()
    {
        // Forum says new version, vnum unchanged — forced launcher flow
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        // First ReadVersion call in Handle, second in HandleUpdatePath after launcher
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum), Result.Success(UpdatedVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        _processDetector.IsLotroLauncherRunning().Returns(true, false);
        _processDetector.IsGameClientRunning().Returns(false);
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));
    }

    // ───────────────────────────── Normal launch (no update, no vnum change) ─────────────────────────────

    [Fact]
    public async Task Handle_NoUpdate_NoVnumChange_ShouldProtectLaunchUnprotect()
    {
        // Arrange
        SetupNoUpdate();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeFalse();
        result.Value.GameExitCode.ShouldBe(GameExitCode);
        result.Value.ForumVersion.ShouldBe(ForumVersion);

        _protector.Received(1).Protect(DatFilePath);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _protector.Received(1).Unprotect(DatFilePath);

        // No re-patching or version saving in normal flow
        _patchingService.DidNotReceive().ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<LotroKoniecDev.Application.OperationProgress>?>());
        _versionStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>());
    }

    // ───────────────────────────── First run — baseline, re-patch, no forced launcher ─────────────────────────────

    [Fact]
    public async Task Handle_FirstRun_ShouldSaveBaselineAndRepatchWithoutForcedLauncher()
    {
        // Arrange — first run: StoredInfo is null
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, null)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        // Should save baseline, re-patch, and launch ONCE (no forced launcher)
        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Vnum changed (game updated outside patcher) ─────────────────────────────

    [Fact]
    public async Task Handle_VnumChanged_ShouldRepatchAndLaunchWithoutForcedLauncher()
    {
        // Arrange
        SetupVnumChanged();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        // Should save new vnum, re-patch, and launch ONCE (no forced launcher)
        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Vnum changed + forum new version — still no forced launcher ─────────────────────────────

    [Fact]
    public async Task Handle_VnumChanged_ForumNewVersion_ShouldRepatchWithoutForcedLauncher()
    {
        // Arrange — game already updated AND forum sees new version
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldVnum)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert — vnum change takes priority, no forced launcher
        result.IsSuccess.ShouldBeTrue();
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
    }

    // ───────────────────────────── Forum new version + vnum unchanged — forced launcher (only case) ─────────────────────────────

    [Fact]
    public async Task Handle_ForumNewVersion_VnumUnchanged_ShouldForceLauncherFlow()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        // Forced launcher flow: ReadVersion(before) + ReadVersion(after) + unprotect(update) + launcher(update) + save + re-patch + protect(best-effort) + protect(game) + launcher(game) + unprotect(game)
        _datVersionReader.Received(2).ReadVersion(DatFilePath);
        await _launcher.Received(2).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
    }

    // ───────────────────────────── Game client detected after wait — Phase 3 safety net ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_GameClientDetectedAfterWait_ShouldKillAndContinue()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();

        // Phase 1: no launcher reappears (timeout)
        // Phase 2: skipped (launcher not running)
        _processDetector.IsLotroLauncherRunning().Returns(false);
        // Phase 3: game client is running → kill it
        _processDetector.IsGameClientRunning().Returns(true, false);
        _processDetector.KillLotroProcesses().Returns(Result.Success());

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _processDetector.Received(1).KillLotroProcesses();
    }

    // ───────────────────────────── Game client detected during Phase 2 — existing behavior ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_GameClientDuringPhase2_ShouldKillAndContinue()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();

        // Phase 1: launcher reappears
        // Phase 2: launcher running, game client detected → kill
        int launcherCallCount = 0;
        _processDetector.IsLotroLauncherRunning().Returns(_ =>
        {
            launcherCallCount++;
            return launcherCallCount <= 2;
        });
        _processDetector.IsGameClientRunning().Returns(true, false);
        _processDetector.KillLotroProcesses().Returns(Result.Success());

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _processDetector.Received(1).KillLotroProcesses();
    }

    // ───────────────────────────── Protect fails — launch not called ─────────────────────────────

    [Fact]
    public async Task Handle_ProtectFails_ShouldReturnFailure_LaunchNotCalled()
    {
        // Arrange
        SetupNoUpdate();
        Error protectError = new("DatFileProtection.ProtectFailed", "Access denied", ErrorType.IoError);
        _protector.Protect(DatFilePath).Returns(Result.Failure(protectError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.ProtectFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Launch fails — unprotect still called ─────────────────────────────

    [Fact]
    public async Task Handle_LaunchFails_ShouldReturnFailure_UnprotectStillCalled()
    {
        // Arrange
        SetupNoUpdate();
        Error launchError = new("GameLaunch.LaunchFailed", "Process.Start returned null", ErrorType.Failure);
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<int>(launchError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.LaunchFailed");
        _protector.Received(1).Unprotect(DatFilePath);
    }

    // ───────────────────────────── Forum check fails — graceful, launches normally ─────────────────────────────

    [Fact]
    public async Task Handle_ForumCheckFails_ShouldLaunchNormally()
    {
        // Arrange — forum fails → no ForumVersion, stored info exists, vnum same
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(null, StoredCurrent)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert — still launches normally, no update path
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
    }

    // ───────────────────────────── Forced update — confirm (save) fails ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_SaveFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();
        Error saveError = new("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData)
            .Returns(Result.Failure(saveError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Re-patch fails after update — DAT re-protected ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_RepatchFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();
        Error patchError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Failure<PatchSummaryResponse>(patchError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Kill fails during update — error propagated, DAT re-protected ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_KillFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupForumUpdateVnumUnchanged();
        _processDetector.IsLotroLauncherRunning().Returns(true, true, false);
        _processDetector.IsGameClientRunning().Returns(true);
        Error killError = new("GameLaunch.KillFailed", "Access denied", ErrorType.Failure);
        _processDetector.KillLotroProcesses().Returns(Result.Failure(killError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.KillFailed");
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── CheckForUpdateAsync I/O failure — propagated ─────────────────────────────

    [Fact]
    public async Task Handle_CheckForUpdateFails_ShouldReturnFailure()
    {
        // Arrange
        Error readError = new("GameUpdateCheck.VersionFileError", "Access denied", ErrorType.IoError);
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Failure<GameUpdateCheckSummary>(readError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
        _protector.DidNotReceive().Protect(Arg.Any<string>());
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Null command ─────────────────────────────

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    // ───────────────────────────── Game already running ─────────────────────────────

    [Fact]
    public async Task Handle_GameAlreadyRunning_ShouldReturnFailure()
    {
        // Arrange
        _processDetector.IsLotroRunning().Returns(true);

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameLaunch.GameAlreadyRunning);
        await _updateChecker.DidNotReceive().CheckForUpdateAsync(Arg.Any<string>());
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Cancellation during update monitoring ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_Cancelled_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        _processDetector.IsLotroLauncherRunning().Returns(false);
        _processDetector.IsGameClientRunning().Returns(false);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), cts.Token);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Forced update — unprotect fails ─────────────────────────────

    [Fact]
    public async Task Handle_ForumUpdate_UnprotectFails_ShouldReturnFailure()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        Error unprotectError = new("DatFileProtection.UnprotectFailed", "Access denied", ErrorType.IoError);
        _protector.Unprotect(DatFilePath).Returns(Result.Failure(unprotectError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.UnprotectFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── DAT version read fails ─────────────────────────────

    [Fact]
    public async Task Handle_DatVersionReadFails_ShouldReturnFailure()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        Error readError = new("DatFile.ReadFailed", "Cannot open DAT", ErrorType.IoError);
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Failure<DatVersionInfo>(readError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.ReadFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Legacy stored version (no vnums) — treated as vnum change ─────────────────────────────

    [Fact]
    public async Task Handle_LegacyStoredVersion_ShouldEnterForcedLauncherFlow()
    {
        // Arrange — legacy version.txt had no vnums (null VnumGameData)
        // ForumVersionChanged is true (40.1 → 40.2), vnumChanged is false (null stored vnum) → forced launcher
        StoredVersionInfo legacyStored = new("40.1", null, null);
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, legacyStored)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum), Result.Success(UpdatedVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        _processDetector.IsLotroLauncherRunning().Returns(true, false);
        _processDetector.IsGameClientRunning().Returns(false);
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert — enters forced launcher flow, launches twice (update + game)
        result.IsSuccess.ShouldBeTrue();
        await _launcher.Received(2).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }
}
