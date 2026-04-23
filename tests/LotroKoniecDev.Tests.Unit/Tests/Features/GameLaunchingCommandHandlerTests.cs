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

    private readonly IGameLaunchingStrategy _strategy;
    private readonly GameLaunchingCommandHandler _sut;

    public GameLaunchingCommandHandlerTests()
    {
        _strategy = Substitute.For<IGameLaunchingStrategy>();
        _sut = new GameLaunchingCommandHandler(_strategy);
    }

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ShouldDelegateToStrategy()
    {
        GameLaunchingResponse response = new(null, false, 0, TranslationsApplied: true, AppliedCount: 50);
        _strategy.ExecuteAsync(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        GameLaunchingCommand command = new(DatFilePath, VersionFilePath, TranslationFilePath);

        Result<GameLaunchingResponse> result = await _sut.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(response);
        await _strategy.Received(1).ExecuteAsync(command, Arg.Any<CancellationToken>());
    }
}

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
