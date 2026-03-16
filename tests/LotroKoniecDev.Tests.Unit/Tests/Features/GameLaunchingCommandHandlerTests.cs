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

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        // Arrange
        LegacyGameLaunchingStrategy legacy = CreateLegacyStrategy();
        SimplifiedGameLaunchingStrategy simplified = CreateSimplifiedStrategy();
        GameLaunchingCommandHandler sut = new(legacy, simplified);

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_UseLegacyFlow_ShouldDelegateToLegacyStrategy()
    {
        // Arrange
        IGameProcessDetector processDetector = Substitute.For<IGameProcessDetector>();
        IGameUpdateChecker updateChecker = Substitute.For<IGameUpdateChecker>();
        IDatVersionReader datVersionReader = Substitute.For<IDatVersionReader>();
        IDatFileProtector protector = Substitute.For<IDatFileProtector>();
        IGameLauncher launcher = Substitute.For<IGameLauncher>();
        IGameVersionFileStore versionStore = Substitute.For<IGameVersionFileStore>();
        IPatchingService patchingService = Substitute.For<IPatchingService>();

        StoredVersionInfo stored = new("40.2", 100, 200);
        updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary("40.2", stored)));
        datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(new DatVersionInfo(100, 200)));
        protector.Protect(DatFilePath).Returns(Result.Success());
        protector.Unprotect(DatFilePath).Returns(Result.Success());
        launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(0));

        LegacyGameLaunchingStrategy legacy = new(
            updateChecker, versionStore, datVersionReader, protector,
            launcher, processDetector, patchingService,
            NullLogger<LegacyGameLaunchingStrategy>.Instance);
        SimplifiedGameLaunchingStrategy simplified = CreateSimplifiedStrategy();
        GameLaunchingCommandHandler sut = new(legacy, simplified);

        GameLaunchingCommand command = new(DatFilePath, VersionFilePath, TranslationFilePath, UseLegacyFlow: true);

        // Act
        Result<GameLaunchingResponse> result = await sut.Handle(command, CancellationToken.None);

        // Assert — legacy strategy was used (it calls LaunchAndWaitForExitAsync)
        result.IsSuccess.ShouldBeTrue();
        await launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DefaultFlow_ShouldDelegateToSimplifiedStrategy()
    {
        // Arrange
        IGameProcessDetector processDetector = Substitute.For<IGameProcessDetector>();
        IFileHasher fileHasher = Substitute.For<IFileHasher>();
        IGameVersionFileStore versionStore = Substitute.For<IGameVersionFileStore>();
        IDatVersionReader datVersionReader = Substitute.For<IDatVersionReader>();
        IPatchingService patchingService = Substitute.For<IPatchingService>();
        IGameLauncher launcher = Substitute.For<IGameLauncher>();

        StoredVersionInfo stored = new("40.2", 100, 200, "abc123");
        fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success("abc123"));
        versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(stored));
        launcher.Launch(DatFilePath).Returns(Result.Success());

        LegacyGameLaunchingStrategy legacy = CreateLegacyStrategy();
        SimplifiedGameLaunchingStrategy simplified = new(
            processDetector, fileHasher, versionStore, datVersionReader,
            patchingService, launcher,
            NullLogger<SimplifiedGameLaunchingStrategy>.Instance);
        GameLaunchingCommandHandler sut = new(legacy, simplified);

        GameLaunchingCommand command = new(DatFilePath, VersionFilePath, TranslationFilePath);

        // Act
        Result<GameLaunchingResponse> result = await sut.Handle(command, CancellationToken.None);

        // Assert — simplified strategy was used (it calls Launch, not LaunchAndWaitForExitAsync)
        result.IsSuccess.ShouldBeTrue();
        launcher.Received(1).Launch(DatFilePath);
    }

    private static LegacyGameLaunchingStrategy CreateLegacyStrategy()
    {
        return new(
            Substitute.For<IGameUpdateChecker>(),
            Substitute.For<IGameVersionFileStore>(),
            Substitute.For<IDatVersionReader>(),
            Substitute.For<IDatFileProtector>(),
            Substitute.For<IGameLauncher>(),
            Substitute.For<IGameProcessDetector>(),
            Substitute.For<IPatchingService>(),
            NullLogger<LegacyGameLaunchingStrategy>.Instance);
    }

    private static SimplifiedGameLaunchingStrategy CreateSimplifiedStrategy()
    {
        return new(
            Substitute.For<IGameProcessDetector>(),
            Substitute.For<IFileHasher>(),
            Substitute.For<IGameVersionFileStore>(),
            Substitute.For<IDatVersionReader>(),
            Substitute.For<IPatchingService>(),
            Substitute.For<IGameLauncher>(),
            NullLogger<SimplifiedGameLaunchingStrategy>.Instance);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Legacy strategy tests — all existing tests adapted from old handler
// ═══════════════════════════════════════════════════════════════════════
public sealed class LegacyGameLaunchingStrategyTests
{
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";
    private const string VersionFilePath = @"C:\temp\version.txt";
    private const string TranslationFilePath = @"C:\translations\polish.txt";
    private const int GameExitCode = 0;
    private const string ForumVersion = "40.2";

    private static readonly DatVersionInfo CurrentVnum = new(100, 200);
    private static readonly DatVersionInfo UpdatedVnum = new(100, 201);
    private static readonly StoredVersionInfo StoredCurrent = new(ForumVersion, 100, 200);
    private static readonly StoredVersionInfo StoredOldForum = new("40.1", 100, 200);
    private static readonly StoredVersionInfo StoredOldVnum = new("40.1", 100, 199);

    private readonly IGameUpdateChecker _updateChecker;
    private readonly IGameVersionFileStore _versionStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IDatFileProtector _protector;
    private readonly IGameLauncher _launcher;
    private readonly IGameProcessDetector _processDetector;
    private readonly IPatchingService _patchingService;
    private readonly LegacyGameLaunchingStrategy _sut;

    public LegacyGameLaunchingStrategyTests()
    {
        _updateChecker = Substitute.For<IGameUpdateChecker>();
        _versionStore = Substitute.For<IGameVersionFileStore>();
        _datVersionReader = Substitute.For<IDatVersionReader>();
        _protector = Substitute.For<IDatFileProtector>();
        _launcher = Substitute.For<IGameLauncher>();
        _processDetector = Substitute.For<IGameProcessDetector>();
        _patchingService = Substitute.For<IPatchingService>();

        _sut = new LegacyGameLaunchingStrategy(
            _updateChecker,
            _versionStore,
            _datVersionReader,
            _protector,
            _launcher,
            _processDetector,
            _patchingService,
            NullLogger<LegacyGameLaunchingStrategy>.Instance);
    }

    private static GameLaunchingCommand CreateCommand() =>
        new(DatFilePath, VersionFilePath, TranslationFilePath, UseLegacyFlow: true);

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
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
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

    [Fact]
    public async Task ExecuteAsync_NoUpdate_NoVnumChange_ShouldProtectLaunchUnprotect()
    {
        SetupNoUpdate();

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeFalse();
        result.Value.GameExitCode.ShouldBe(GameExitCode);
        result.Value.ForumVersion.ShouldBe(ForumVersion);

        _protector.Received(1).Protect(DatFilePath);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _protector.Received(1).Unprotect(DatFilePath);

        _patchingService.DidNotReceive().ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<LotroKoniecDev.Application.OperationProgress>?>());
        _versionStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_FirstRun_ShouldSaveBaselineAndRepatchWithoutForcedLauncher()
    {
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

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_VnumChanged_ShouldRepatchAndLaunchWithoutForcedLauncher()
    {
        SetupVnumChanged();

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_VnumChanged_ForumNewVersion_ShouldRepatchWithoutForcedLauncher()
    {
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

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _launcher.Received(1).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
    }

    [Fact]
    public async Task ExecuteAsync_ForumNewVersion_VnumUnchanged_ShouldForceLauncherFlow()
    {
        SetupForumUpdateVnumUnchanged();

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeTrue();

        _datVersionReader.Received(2).ReadVersion(DatFilePath);
        await _launcher.Received(2).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
        _versionStore.Received(1).SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData);
        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_GameClientDetectedAfterWait_ShouldKillAndContinue()
    {
        SetupForumUpdateVnumUnchanged();

        _processDetector.IsLotroLauncherRunning().Returns(false);
        _processDetector.IsGameClientRunning().Returns(true, false);
        _processDetector.KillLotroProcesses().Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _processDetector.Received(1).KillLotroProcesses();
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_GameClientDuringPhase2_ShouldKillAndContinue()
    {
        SetupForumUpdateVnumUnchanged();

        int launcherCallCount = 0;
        _processDetector.IsLotroLauncherRunning().Returns(_ =>
        {
            launcherCallCount++;
            return launcherCallCount <= 2;
        });
        _processDetector.IsGameClientRunning().Returns(true, false);
        _processDetector.KillLotroProcesses().Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _processDetector.Received(1).KillLotroProcesses();
    }

    [Fact]
    public async Task ExecuteAsync_ProtectFails_ShouldReturnFailure_LaunchNotCalled()
    {
        SetupNoUpdate();
        Error protectError = new("DatFileProtection.ProtectFailed", "Access denied", ErrorType.IoError);
        _protector.Protect(DatFilePath).Returns(Result.Failure(protectError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.ProtectFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LaunchFails_ShouldReturnFailure_UnprotectStillCalled()
    {
        SetupNoUpdate();
        Error launchError = new("GameLaunch.LaunchFailed", "Process.Start returned null", ErrorType.Failure);
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<int>(launchError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.LaunchFailed");
        _protector.Received(1).Unprotect(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_ForumCheckFails_ShouldLaunchNormally()
    {
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(null, StoredCurrent)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _protector.Protect(DatFilePath).Returns(Result.Success());
        _protector.Unprotect(DatFilePath).Returns(Result.Success());
        _launcher.LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>())
            .Returns(Result.Success(GameExitCode));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateWasDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_SaveFails_ShouldReturnFailure_DatReprotected()
    {
        SetupForumUpdateVnumUnchanged();
        Error saveError = new("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _versionStore.SaveVersion(VersionFilePath, ForumVersion, UpdatedVnum.VnumDatFile, UpdatedVnum.VnumGameData)
            .Returns(Result.Failure(saveError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
        _protector.Received().Protect(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_RepatchFails_ShouldReturnFailure_DatReprotected()
    {
        SetupForumUpdateVnumUnchanged();
        Error patchError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Failure<PatchSummaryResponse>(patchError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");
        _protector.Received().Protect(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_KillFails_ShouldReturnFailure_DatReprotected()
    {
        SetupForumUpdateVnumUnchanged();
        _processDetector.IsLotroLauncherRunning().Returns(true, true, false);
        _processDetector.IsGameClientRunning().Returns(true);
        Error killError = new("GameLaunch.KillFailed", "Access denied", ErrorType.Failure);
        _processDetector.KillLotroProcesses().Returns(Result.Failure(killError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.KillFailed");
        _protector.Received().Protect(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_CheckForUpdateFails_ShouldReturnFailure()
    {
        Error readError = new("GameUpdateCheck.VersionFileError", "Access denied", ErrorType.IoError);
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Failure<GameUpdateCheckSummary>(readError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
        _protector.DidNotReceive().Protect(Arg.Any<string>());
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GameAlreadyRunning_ShouldReturnFailure()
    {
        _processDetector.IsLotroRunning().Returns(true);

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameLaunch.GameAlreadyRunning);
        await _updateChecker.DidNotReceive().CheckForUpdateAsync(Arg.Any<string>());
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_Cancelled_ShouldReturnFailure_DatReprotected()
    {
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

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");
        _protector.Received().Protect(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_ForumUpdate_UnprotectFails_ShouldReturnFailure()
    {
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        Error unprotectError = new("DatFileProtection.UnprotectFailed", "Access denied", ErrorType.IoError);
        _protector.Unprotect(DatFilePath).Returns(Result.Failure(unprotectError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.UnprotectFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DatVersionReadFails_ShouldReturnFailure()
    {
        _updateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(ForumVersion, StoredOldForum)));
        Error readError = new("DatFile.ReadFailed", "Cannot open DAT", ErrorType.IoError);
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Failure<DatVersionInfo>(readError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.ReadFailed");
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LegacyStoredVersion_ShouldEnterForcedLauncherFlow()
    {
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

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _launcher.Received(2).LaunchAndWaitForExitAsync(DatFilePath, Arg.Any<CancellationToken>());
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Simplified strategy tests — new flow
// ═══════════════════════════════════════════════════════════════════════
public sealed class SimplifiedGameLaunchingStrategyTests
{
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";
    private const string VersionFilePath = @"C:\temp\version.txt";
    private const string TranslationFilePath = @"C:\translations\polish.txt";
    private const string TranslationHash = "abc123def456";
    private const string NewTranslationHash = "xyz789";

    private static readonly DatVersionInfo CurrentVnum = new(100, 200);
    private static readonly StoredVersionInfo StoredWithHash = new("40.2", 100, 200, TranslationHash);
    private static readonly StoredVersionInfo StoredWithoutHash = new("40.2", 100, 200);

    private readonly IGameProcessDetector _processDetector;
    private readonly IFileHasher _fileHasher;
    private readonly IGameVersionFileStore _versionStore;
    private readonly IDatVersionReader _datVersionReader;
    private readonly IPatchingService _patchingService;
    private readonly IGameLauncher _launcher;
    private readonly SimplifiedGameLaunchingStrategy _sut;

    public SimplifiedGameLaunchingStrategyTests()
    {
        _processDetector = Substitute.For<IGameProcessDetector>();
        _fileHasher = Substitute.For<IFileHasher>();
        _versionStore = Substitute.For<IGameVersionFileStore>();
        _datVersionReader = Substitute.For<IDatVersionReader>();
        _patchingService = Substitute.For<IPatchingService>();
        _launcher = Substitute.For<IGameLauncher>();

        _sut = new SimplifiedGameLaunchingStrategy(
            _processDetector,
            _fileHasher,
            _versionStore,
            _datVersionReader,
            _patchingService,
            _launcher,
            NullLogger<SimplifiedGameLaunchingStrategy>.Instance);
    }

    private static GameLaunchingCommand CreateCommand() =>
        new(DatFilePath, VersionFilePath, TranslationFilePath);

    [Fact]
    public async Task ExecuteAsync_GameAlreadyRunning_ShouldReturnFailure()
    {
        _processDetector.IsLotroRunning().Returns(true);

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameLaunch.GameAlreadyRunning);
        _launcher.DidNotReceive().Launch(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteAsync_TranslationUnchanged_ShouldSkipPatchAndLaunch()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeFalse();
        result.Value.AppliedCount.ShouldBe(0);
        result.Value.GameExitCode.ShouldBe(0);

        _patchingService.DidNotReceive().ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<LotroKoniecDev.Application.OperationProgress>?>());
        _launcher.Received(1).Launch(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_TranslationChanged_ShouldPatchSaveAndLaunch()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(NewTranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 90, 10, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, NewTranslationHash)
            .Returns(Result.Success());
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeTrue();
        result.Value.AppliedCount.ShouldBe(90);
        result.Value.SkippedCount.ShouldBe(10);

        _patchingService.Received(1).ApplyTranslations(TranslationFilePath, DatFilePath, null);
        _versionStore.Received(1).SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, NewTranslationHash);
        _launcher.Received(1).Launch(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_FirstRun_NoStoredHash_ShouldPatch()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(null));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(50, 50, 0, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, Arg.Any<string?>(), CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, TranslationHash)
            .Returns(Result.Success());
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeTrue();
        result.Value.AppliedCount.ShouldBe(50);
    }

    [Fact]
    public async Task ExecuteAsync_StoredWithoutHash_ShouldPatch()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithoutHash));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(50, 50, 0, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, TranslationHash)
            .Returns(Result.Success());
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TranslationsApplied.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_HashComputeFails_ShouldReturnFailure()
    {
        Error hashError = new("GameUpdateCheck.VersionFileError", "File not found", ErrorType.IoError);
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Failure<string>(hashError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        _launcher.DidNotReceive().Launch(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteAsync_PatchFails_ShouldReturnFailure()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(NewTranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        Error patchError = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Failure<PatchSummaryResponse>(patchError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("GameLaunch");
        _launcher.DidNotReceive().Launch(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteAsync_LaunchFails_ShouldReturnFailure()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        Error launchError = new("GameLaunch.LaunchFailed", "Not found", ErrorType.Failure);
        _launcher.Launch(DatFilePath).Returns(Result.Failure(launchError));

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameLaunch.LaunchFailed");
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_ShouldNotWaitForExit()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(TranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.GameExitCode.ShouldBe(0);

        // Should NOT call LaunchAndWaitForExitAsync
        await _launcher.DidNotReceive().LaunchAndWaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _launcher.Received(1).Launch(DatFilePath);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseToString_TranslationsApplied()
    {
        _fileHasher.ComputeHash(TranslationFilePath).Returns(Result.Success(NewTranslationHash));
        _versionStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredWithHash));
        _patchingService.ApplyTranslations(TranslationFilePath, DatFilePath, null)
            .Returns(Result.Success(new PatchSummaryResponse(100, 90, 10, [])));
        _datVersionReader.ReadVersion(DatFilePath).Returns(Result.Success(CurrentVnum));
        _versionStore.SaveVersion(VersionFilePath, "40.2", CurrentVnum.VnumDatFile, CurrentVnum.VnumGameData, NewTranslationHash)
            .Returns(Result.Success());
        _launcher.Launch(DatFilePath).Returns(Result.Success());

        Result<GameLaunchingResponse> result = await _sut.ExecuteAsync(CreateCommand(), CancellationToken.None);

        result.Value.ToString().ShouldContain("90 applied");
        result.Value.ToString().ShouldContain("10 skipped");
    }
}
