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
    private const string StoredVersion = "40.1";

    private static readonly DatVersionInfo PreviousVnum = new(100, 200);
    private static readonly DatVersionInfo UpdatedVnum = new(100, 201);

    private readonly IGameUpdateChecker _updateChecker;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IDatFileProtector _protector;
    private readonly IGameLauncher _launcher;
    private readonly IGameProcessDetector _processDetector;
    private readonly IPatchingService _patchingService;
    private readonly GameLaunchingCommandHandler _sut;

    public GameLaunchingCommandHandlerTests()
    {
        _updateChecker = Substitute.For<IGameUpdateChecker>();
        _datVersionReader = Substitute.For<IDatVersionReader>();
        _protector = Substitute.For<IDatFileProtector>();
        _launcher = Substitute.For<IGameLauncher>();
        _processDetector = Substitute.For<IGameProcessDetector>();
        _patchingService = Substitute.For<IPatchingService>();

        _sut = new GameLaunchingCommandHandler(
            _updateChecker,
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
            .Returns(Result.Success(new GameUpdateCheckSummary(false, ForumVersion, StoredVersion)));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
    }

    private void SetupUpdateDetected()
    {
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, ForumVersion, StoredVersion)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(PreviousVnum), Result.Success(UpdatedVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        // Launcher appears (Phase 1 breaks immediately) then disappears (Phase 2 skips)
        _processDetector.IsLotroLauncherRunning().Returns(true, false);
        _processDetector.IsGameClientRunning().Returns(false);
        _updateChecker.ConfirmUpdateInstalled(VersionFilePath, ForumVersion, false, PreviousVnum, UpdatedVnum)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));
    }

    // ───────────────────────────── Normal launch (no update) ─────────────────────────────

    [Fact]
    public async Task Handle_NoUpdate_ShouldProtectLaunchUnprotect()
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
    }

    // ───────────────────────────── Update detected — full orchestration ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_ShouldOrchestrateFull()
    {
        // Arrange
        SetupUpdateDetected();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe(ForumVersion);
        result.Value.GameExitCode.ShouldBe(GameExitCode);

        // Verify full orchestration:
        // snapshot(before) → unprotect → launch(update) → snapshot(after) → confirm → re-patch → protect(best-effort) → protect → launch(game) → unprotect(finally)
        _datVersionReader.Received(2).ReadVersion(DatFilePath); // before + after
        _protector.Received(2).Unprotect(DatFilePath); // step 2 (for update via best-effort re-protect) + finally (after game)
        await _launcher.Received(2).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>()); // once for update, once for game
        _updateChecker.Received(1).ConfirmUpdateInstalled(VersionFilePath, ForumVersion, false, PreviousVnum, UpdatedVnum);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        _protector.Received(2).Protect(DatFilePath); // best-effort after update try/finally + before game launch
    }

    // ───────────────────────────── Update detected — first run ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_FirstRun_ShouldSucceed()
    {
        // Arrange — first run: StoredVersion is null
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, ForumVersion, null)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(PreviousVnum), Result.Success(UpdatedVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        _processDetector.IsLotroLauncherRunning().Returns(true, false);
        _updateChecker.ConfirmUpdateInstalled(VersionFilePath, ForumVersion, true, PreviousVnum, UpdatedVnum)
            .Returns(Result.Success());
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 95, 5, [])));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();
    }

    // ───────────────────────────── Update detected — confirm fails ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_ConfirmFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupUpdateDetected();
        Error saveError = new("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _updateChecker.ConfirmUpdateInstalled(VersionFilePath, ForumVersion, false, PreviousVnum, UpdatedVnum)
            .Returns(Result.Failure(saveError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");

        // DAT must be re-protected after failure (via try/finally)
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Game client detected during update ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_GameClientDetected_ShouldKillAndContinue()
    {
        // Arrange
        SetupUpdateDetected();

        // Simulate: launcher is running, then game client appears, kill succeeds, launcher stops
        int launcherCallCount = 0;
        _processDetector.IsLotroLauncherRunning().Returns(_ =>
        {
            launcherCallCount++;
            return launcherCallCount <= 2; // running for first 2 checks, then stops
        });
        _processDetector.IsGameClientRunning().Returns(true);
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
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(false, ForumVersion, StoredVersion)));
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
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(false, ForumVersion, StoredVersion)));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        Error launchError = new("GameLaunch.LaunchFailed", "Process.Start returned null", ErrorType.Failure);
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<int>(launchError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.LaunchFailed");

        // Unprotect must still be called (try/finally)
        _protector.Received(1).Unprotect(DatFilePath);
    }

    // ───────────────────────────── Forum check fails — graceful, launches normally ─────────────────────────────

    [Fact]
    public async Task Handle_ForumCheckFails_ShouldLaunchNormally()
    {
        // Arrange — forum fails → CheckForUpdateAsync returns UpdateDetected=false
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(false, null, StoredVersion)));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert — still launches, no update path taken
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
        _datVersionReader.DidNotReceive().ReadVersion(Arg.Any<string>());
    }

    // ───────────────────────────── Re-patch fails after update — DAT re-protected ─────────────────────────────

    [Fact]
    public async Task Handle_RepatchFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupUpdateDetected();
        Error patchError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Failure<PatchSummaryResponse>(patchError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");

        // DAT must be re-protected after re-patch failure (via try/finally)
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Kill fails during update — error propagated, DAT re-protected ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_KillFails_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        SetupUpdateDetected();

        // Simulate: launcher running, game client detected, kill fails
        _processDetector.IsLotroLauncherRunning().Returns(true, true, false);
        _processDetector.IsGameClientRunning().Returns(true);
        Error killError = new("GameLaunch.KillFailed", "Access denied", ErrorType.Failure);
        _processDetector.KillLotroProcesses().Returns(Result.Failure(killError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.KillFailed");

        // DAT must be re-protected after kill failure (via try/finally)
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
    public async Task Handle_UpdateDetected_Cancelled_ShouldReturnFailure_DatReprotected()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, ForumVersion, StoredVersion)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(PreviousVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));
        _processDetector.IsLotroLauncherRunning().Returns(false);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), cts.Token);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");

        // DAT must be re-protected after cancellation (via try/finally)
        _protector.Received().Protect(DatFilePath);
    }

    // ───────────────────────────── Update detected — unprotect fails ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_UnprotectFails_ShouldReturnFailure()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, ForumVersion, StoredVersion)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(PreviousVnum));
        Error unprotectError = new("DatFileProtection.UnprotectFailed", "Access denied", ErrorType.IoError);
        _protector.Unprotect(DatFilePath).Returns(Result.Failure(unprotectError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.UnprotectFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── Update detected — DAT version read fails ─────────────────────────────

    [Fact]
    public async Task Handle_UpdateDetected_DatVersionReadFails_ShouldReturnFailure()
    {
        // Arrange
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, ForumVersion, StoredVersion)));
        Error readError = new("DatFile.ReadFailed", "Cannot open DAT", ErrorType.IoError);
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Failure<DatVersionInfo>(readError));

        // Act
        Result<GameLaunchingResponse> result = await _sut.Handle(CreateCommand(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.ReadFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
