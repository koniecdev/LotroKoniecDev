using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Tests.Unit.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class ExporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDatFileHandler _mockHandler;
    private readonly Exporter _exporter;

    public ExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LotroExporterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _mockHandler = Substitute.For<IDatFileHandler>();
        _exporter = new Exporter(_mockHandler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExportAllTexts_SuccessfulExport_ShouldReturnSummary()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, "Test text")));

        // Act
        Result<ExportSummaryResponse> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(1);
        result.Value.OutputPath.ShouldBe(outputPath);

        _mockHandler.Received(1).Close(0);
    }

    [Fact]
    public void ExportAllTexts_DatFileOpenFails_ShouldReturnFailure()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");

        Error error = new("DatFile.CannotOpen", "Cannot open", ErrorType.IoError);
        _mockHandler.Open(datPath).Returns(Result.Failure<int>(error));

        // Act
        Result<ExportSummaryResponse> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
    }

    [Fact]
    public void ExportAllTexts_NullDatPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _exporter.ExportAllTexts(null!, "output.txt");
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ExportAllTexts_NullOutputPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _exporter.ExportAllTexts("input.dat", null!);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ExportAllTexts_NonTextFilesSkipped_ShouldOnlyExportTextFiles()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open(datPath).Returns(Result.Success(0));
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

        // Act
        Result<ExportSummaryResponse> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(2);
        _mockHandler.Received(2).GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ExportAllTexts_WithProgressCallback_ShouldReportProgress()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");
        List<(int Processed, int Total)> progressReports = new();

        Dictionary<int, (int, int)> fileSizes = Enumerable.Range(1, 600)
            .ToDictionary(
                i => 0x25000000 + i,
                i => (100, 1));

        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(Arg.Any<int>()).Returns(fileSizes);
        _mockHandler.GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(x => Result.Success(TestDataFactory.CreateTextSubFileData((int)x[1], "Test")));

        // Act
        Result<ExportSummaryResponse> result = _exporter.ExportAllTexts(datPath, outputPath,
            (processed, total) => progressReports.Add((processed, total)));

        // Assert
        result.IsSuccess.ShouldBeTrue();
        progressReports.ShouldNotBeEmpty();
        progressReports[0].Processed.ShouldBe(500);
        progressReports[0].Total.ShouldBe(600);
    }

    [Fact]
    public void ExportAllTexts_GetSubfileDataFails_ShouldContinueWithOtherFiles()
    {
        // Arrange — 2 text files, first one fails to load
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");

        Error loadError = new("SubFile.ParseError", "Corrupted", ErrorType.IoError);

        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) },
            { 0x25000002, (100, 1) }
        });
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Failure<byte[]>(loadError));
        _mockHandler.GetSubfileData(0, 0x25000002, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000002, "Working text")));

        // Act
        Result<ExportSummaryResponse> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert — should succeed with partial results, not fail entirely
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(2);
    }

    [Fact]
    public void Constructor_NullHandler_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Exporter(null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    private string CreateTempFile(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }
}
