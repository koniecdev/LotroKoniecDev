using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.PreflightChecking;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class PreflightReportResponseTests
{
    private static readonly string DatFilePath = Path.Combine("game", "client_local_English.dat");

    public static TheoryData<Result<GameUpdateCheckSummary>?> AnyUpdateCheckOutcome =>
    [
        null,
        Result.Failure<GameUpdateCheckSummary>(DomainErrors.GameUpdateCheck.NetworkError("timeout")),
        Result.Success(new GameUpdateCheckSummary(null, null)),
        Result.Success(new GameUpdateCheckSummary("49.7", new StoredVersionInfo("47.2", 100, 200)))
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnsureCanPatch_GameRunning_ShouldFailWithGameRunning(bool hasWriteAccess)
    {
        // Arrange
        PreflightReportResponse report = new(IsGameRunning: true, hasWriteAccess, GameUpdateCheckResult: null);

        // Act
        Result result = report.EnsureCanPatch(DatFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.DatFileLocation.GameRunning);
    }

    [Fact]
    public void EnsureCanPatch_NoWriteAccess_ShouldFailNamingTheDatFile()
    {
        // Arrange
        PreflightReportResponse report = new(IsGameRunning: false, HasWriteAccess: false, GameUpdateCheckResult: null);

        // Act
        Result result = report.EnsureCanPatch(DatFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.DatFileLocation.NoWriteAccess(DatFilePath));
    }

    [Theory]
    [MemberData(nameof(AnyUpdateCheckOutcome))]
    public void EnsureCanPatch_GameClosedAndFolderWritable_ShouldSucceedWhateverTheUpdateCheckSays(
        Result<GameUpdateCheckSummary>? gameUpdateCheckResult)
    {
        // Arrange
        PreflightReportResponse report = new(IsGameRunning: false, HasWriteAccess: true, gameUpdateCheckResult);

        // Act
        Result result = report.EnsureCanPatch(DatFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureCanPatch_BlankDatFilePath_ShouldThrow(string datFilePath)
    {
        // Arrange
        PreflightReportResponse report = new(IsGameRunning: false, HasWriteAccess: true, GameUpdateCheckResult: null);

        // Act & Assert
        Should.Throw<ArgumentException>(() => report.EnsureCanPatch(datFilePath));
    }
}
