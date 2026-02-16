using FluentValidation;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Tests.Unit.Shared;
using NSubstitute.ExceptionExtensions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class ExportTextsQueryHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDatFileHandler _mockHandler;
    private readonly ExportTextsQueryHandler _sut;

    public ExportTextsQueryHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LotroExportHandlerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _mockHandler = Substitute.For<IDatFileHandler>();
        _sut = new ExportTextsQueryHandler(_mockHandler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Handle_SuccessfulExport_ShouldReturnSummary()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open("test.dat").Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, "Test text")));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(1);
        result.Value.TotalFragments.ShouldBe(1);
        result.Value.OutputPath.ShouldBe(outputPath);
    }

    [Fact]
    public async Task Handle_DatFileOpenFails_ShouldReturnFailure()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");
        Error error = new("DatFile.CannotOpen", "Cannot open", ErrorType.IoError);
        _mockHandler.Open("test.dat").Returns(Result.Failure<int>(error));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
    }

    [Fact]
    public async Task Handle_EmptyDatFilePath_ShouldThrowValidationException()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");
        ExportTextsQuery query = new("", outputPath);

        // Act & Assert
        await Should.ThrowAsync<ValidationException>(() =>
            _sut.Handle(query, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_EmptyOutputPath_ShouldThrowValidationException()
    {
        // Arrange
        ExportTextsQuery query = new("test.dat", "");

        // Act & Assert
        await Should.ThrowAsync<ValidationException>(() =>
            _sut.Handle(query, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_NullQuery_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() =>
            _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_NonTextFilesSkipped_ShouldOnlyExportTextFiles()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open("test.dat").Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) },
            { 0x10000001, (200, 1) },
            { 0x25000002, (100, 1) }
        });
        _mockHandler.GetSubfileData(Arg.Any<int>(), 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, "Text1")));
        _mockHandler.GetSubfileData(Arg.Any<int>(), 0x25000002, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000002, "Text2")));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(2);
        result.Value.TotalFragments.ShouldBe(2);
    }

    [Fact]
    public async Task Handle_WithProgressCallback_ShouldReportProgress()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        Dictionary<int, (int, int)> fileSizes = Enumerable.Range(1, 600)
            .ToDictionary(
                i => 0x25000000 + i,
                i => (100, 1));

        _mockHandler.Open("test.dat").Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(Arg.Any<int>()).Returns(fileSizes);
        _mockHandler.GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(x => Result.Success(TestDataFactory.CreateTextSubFileData((int)x[1], "Test")));

        IProgress<OperationProgress> progress = Substitute.For<IProgress<OperationProgress>>();
        ExportTextsQuery query = new("test.dat", outputPath, progress);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        progress.Received(1).Report(Arg.Is<OperationProgress>(p => p.Current == 500 && p.Total == 600));
    }

    [Fact]
    public async Task Handle_GetSubfileDataFails_ShouldContinueWithOtherFiles()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");
        Error loadError = new("SubFile.ParseError", "Corrupted", ErrorType.IoError);

        _mockHandler.Open("test.dat").Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) },
            { 0x25000002, (100, 1) }
        });
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Failure<byte[]>(loadError));
        _mockHandler.GetSubfileData(0, 0x25000002, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000002, "Working text")));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert — should succeed with partial results, not fail entirely
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(2);
        result.Value.TotalFragments.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_ExceptionDuringExport_ShouldStillCloseDatFile()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open("test.dat").Returns(Result.Success(42));
        _mockHandler.GetAllSubfileSizes(42)
            .Throws(new InvalidOperationException("Simulated failure"));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        _mockHandler.Received(1).Close(42);
    }

}
