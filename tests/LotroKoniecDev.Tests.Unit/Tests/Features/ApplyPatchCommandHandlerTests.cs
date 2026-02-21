using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class ApplyPatchCommandHandlerTests
{
    private readonly IFileProvider _fileProvider;
    private readonly IPatcher _patcher;
    private readonly IBackupManager _backupManager;
    private readonly IPreflightChecker _preflightChecker;
    private readonly IOperationStatusReporter _statusReporter;
    private readonly IProgress<OperationProgress> _progressReporter;
    private readonly ApplyPatchCommandHandler _sut;

    public ApplyPatchCommandHandlerTests()
    {
        _fileProvider = Substitute.For<IFileProvider>();
        _patcher = Substitute.For<IPatcher>();
        _backupManager = Substitute.For<IBackupManager>();
        _preflightChecker = Substitute.For<IPreflightChecker>();
        _statusReporter = Substitute.For<IOperationStatusReporter>();
        _progressReporter = Substitute.For<IProgress<OperationProgress>>();
        
        _sut = new ApplyPatchCommandHandler(
            _statusReporter,
            _fileProvider,
            _patcher,
            _backupManager,
            _preflightChecker,
            _progressReporter);
    }

    private static ApplyPatchCommand CreateCommand(
        string translationsPath = "/translations/polish.txt",
        string datFilePath = "/game/client_local_English.dat",
        string versionFilePath = "/data/version.txt") =>
        new(translationsPath, datFilePath, versionFilePath);

    private void SetupAllPassingDefaults()
    {
        _fileProvider.Exists(Arg.Any<string>()).Returns(true);
        _preflightChecker.RunAllAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _backupManager.Create(Arg.Any<string>()).Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_HappyPath_ShouldReturnPatchSummary()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        PatchSummaryResponse expectedSummary = new(100, 95, 5, []);
        _patcher.ApplyTranslations(command.TranslationsPath, command.DatFilePath, Arg.Any<Action<int, int>?>())
            .Returns(Result.Success(expectedSummary));

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTranslations.ShouldBe(100);
        result.Value.AppliedTranslations.ShouldBe(95);
        result.Value.SkippedTranslations.ShouldBe(5);
    }

    [Fact]
    public async Task Handle_TranslationFileNotFound_ShouldReturnFailure()
    {
        // Arrange
        _fileProvider.Exists(Arg.Is<string>(p => p.Contains("polish"))).Returns(false);
        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.FileNotFound");
    }

    [Fact]
    public async Task Handle_DatFileNotFound_ShouldReturnFailure()
    {
        // Arrange
        _fileProvider.Exists(Arg.Is<string>(p => p.Contains("polish"))).Returns(true);
        _fileProvider.Exists(Arg.Is<string>(p => p.Contains("client_local"))).Returns(false);
        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.NotFound");
    }

    [Fact]
    public async Task Handle_PreflightFails_ShouldReturnFailure()
    {
        // Arrange
        _fileProvider.Exists(Arg.Any<string>()).Returns(true);
        _preflightChecker.RunAllAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        _backupManager.DidNotReceive().Create(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_BackupFails_ShouldReturnFailure()
    {
        // Arrange
        _fileProvider.Exists(Arg.Any<string>()).Returns(true);
        _preflightChecker.RunAllAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        Error backupError = new("Backup.CannotCreate", "Disk full", ErrorType.IoError);
        _backupManager.Create(Arg.Any<string>()).Returns(Result.Failure(backupError));

        ApplyPatchCommand command = CreateCommand();

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Backup.CannotCreate");
    }

    [Fact]
    public async Task Handle_PatcherFails_ShouldRestoreBackupAndReturnFailure()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        Error patchError = new("DatFile.CannotOpen", "Locked", ErrorType.IoError);
        _patcher.ApplyTranslations(command.TranslationsPath, command.DatFilePath, Arg.Any<Action<int, int>?>())
            .Returns(Result.Failure<PatchSummaryResponse>(patchError));

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
        _backupManager.Received(1).Restore(command.DatFilePath);
    }

    [Fact]
    public async Task Handle_PatcherSucceeds_ShouldNotRestoreBackup()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        _patcher.ApplyTranslations(command.TranslationsPath, command.DatFilePath, Arg.Any<Action<int, int>?>())
            .Returns(Result.Success(new PatchSummaryResponse(10, 10, 0, [])));

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _backupManager.DidNotReceive().Restore(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WithWarnings_ShouldReportThem()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        List<string> warnings = ["Fragment 999 not found", "File 0x25000002 not found"];
        _patcher.ApplyTranslations(command.TranslationsPath, command.DatFilePath, Arg.Any<Action<int, int>?>())
            .Returns(Result.Success(new PatchSummaryResponse(10, 8, 2, warnings)));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _statusReporter.Received(1).Report("Fragment 999 not found");
        _statusReporter.Received(1).Report("File 0x25000002 not found");
        _statusReporter.Received(1).Report("Skipped 2 translations");
    }

    [Fact]
    public async Task Handle_NoSkipped_ShouldNotReportSkipped()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        _patcher.ApplyTranslations(command.TranslationsPath, command.DatFilePath, Arg.Any<Action<int, int>?>())
            .Returns(Result.Success(new PatchSummaryResponse(10, 10, 0, [])));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _statusReporter.DidNotReceive().Report(Arg.Is<string>(s => s.Contains("Skipped")));
    }

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ExecutionOrder_ShouldBePreflightThenBackupThenPatch()
    {
        // Arrange
        SetupAllPassingDefaults();
        ApplyPatchCommand command = CreateCommand();

        List<string> callOrder = [];

        _preflightChecker.RunAllAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ =>
            {
                callOrder.Add("preflight");
                return true;
            });

        _backupManager.Create(Arg.Any<string>())
            .Returns(_ =>
            {
                callOrder.Add("backup");
                return Result.Success();
            });

        _patcher.ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Action<int, int>?>())
            .Returns(_ =>
            {
                callOrder.Add("patch");
                return Result.Success(new PatchSummaryResponse(1, 1, 0, []));
            });

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        callOrder.ShouldBe(["preflight", "backup", "patch"]);
    }
}
