using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.PreflightCheck;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class PreflightCheckQueryHandlerTests
{
    private const string DatFilePath = @"C:\game\client_local_English.dat";
    private const string VersionFilePath = @"C:\data\version.txt";

    private readonly IGameUpdateChecker _mockUpdateChecker;
    private readonly IGameProcessDetector _mockProcessDetector;
    private readonly IWriteAccessChecker _mockWriteAccessChecker;
    private readonly PreflightCheckQueryHandler _sut;

    public PreflightCheckQueryHandlerTests()
    {
        _mockUpdateChecker = Substitute.For<IGameUpdateChecker>();
        _mockProcessDetector = Substitute.For<IGameProcessDetector>();
        _mockWriteAccessChecker = Substitute.For<IWriteAccessChecker>();

        _sut = new PreflightCheckQueryHandler(
            _mockUpdateChecker,
            _mockProcessDetector,
            _mockWriteAccessChecker);
    }

    private static PreflightCheckQuery CreateQuery(
        string datFilePath = DatFilePath,
        string versionFilePath = VersionFilePath) =>
        new(datFilePath, versionFilePath);

    [Fact]
    public async Task Handle_AllChecksPass_ShouldReturnSuccessReport()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(@"C:\game").Returns(true);
        _mockUpdateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        PreflightCheckQuery query = CreateQuery();

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsGameRunning.ShouldBeFalse();
        result.Value.HasWriteAccess.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.IsSuccess.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.Value.UpdateDetected.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_GameIsRunning_ShouldReportGameRunning()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(true);
        _mockWriteAccessChecker.CanWriteTo(Arg.Any<string>()).Returns(true);
        _mockUpdateChecker.CheckForUpdateAsync(Arg.Any<string>())
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(CreateQuery(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsGameRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_NoWriteAccess_ShouldReportNoWriteAccess()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(@"C:\game").Returns(false);
        _mockUpdateChecker.CheckForUpdateAsync(Arg.Any<string>())
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(CreateQuery(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.HasWriteAccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_UpdateDetected_ShouldReportUpdateInResult()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(Arg.Any<string>()).Returns(true);
        _mockUpdateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Success(new GameUpdateCheckSummary(true, "40.2", "40.1")));

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(CreateQuery(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.IsSuccess.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.Value.UpdateDetected.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.Value.CurrentVersion.ShouldBe("40.2");
        result.Value.GameUpdateCheckResult!.Value.PreviousVersion.ShouldBe("40.1");
    }

    [Fact]
    public async Task Handle_UpdateCheckFails_ShouldStillReturnSuccessWithFailedUpdateResult()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(Arg.Any<string>()).Returns(true);

        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockUpdateChecker.CheckForUpdateAsync(VersionFilePath)
            .Returns(Result.Failure<GameUpdateCheckSummary>(networkError));

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(CreateQuery(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.IsFailure.ShouldBeTrue();
        result.Value.GameUpdateCheckResult!.Error.Code.ShouldBe("GameUpdateCheck.NetworkError");
    }

    [Fact]
    public async Task Handle_DatFilePathWithNoDirectory_ShouldCheckWriteAccessOnEmptyString()
    {
        // Arrange — Path.GetDirectoryName("file.dat") returns "" on Windows
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo("").Returns(false);
        _mockUpdateChecker.CheckForUpdateAsync(Arg.Any<string>())
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        PreflightCheckQuery query = new("file.dat", VersionFilePath);

        // Act
        Result<PreflightReportResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.HasWriteAccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_ShouldPassVersionFilePathToUpdateChecker()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(Arg.Any<string>()).Returns(true);
        _mockUpdateChecker.CheckForUpdateAsync(Arg.Any<string>())
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        string customVersionPath = @"C:\custom\version.txt";
        PreflightCheckQuery query = new(DatFilePath, customVersionPath);

        // Act
        await _sut.Handle(query, CancellationToken.None);

        // Assert
        await _mockUpdateChecker.Received(1).CheckForUpdateAsync(customVersionPath);
    }

    [Fact]
    public async Task Handle_ShouldCheckWriteAccessOnDatFileDirectory()
    {
        // Arrange
        _mockProcessDetector.IsLotroRunning().Returns(false);
        _mockWriteAccessChecker.CanWriteTo(Arg.Any<string>()).Returns(true);
        _mockUpdateChecker.CheckForUpdateAsync(Arg.Any<string>())
            .Returns(Result.Success(new GameUpdateCheckSummary(false, "40.1", "40.1")));

        PreflightCheckQuery query = new(@"D:\lotro\data\client_local_English.dat", VersionFilePath);

        // Act
        await _sut.Handle(query, CancellationToken.None);

        // Assert
        _mockWriteAccessChecker.Received(1).CanWriteTo(@"D:\lotro\data");
    }
}
