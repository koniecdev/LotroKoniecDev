using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Application.Features.Export;

namespace LotroKoniecDev.Tests.Integration.Features;

public class ExporterTests : IDisposable
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
            { 0x25000001, (100, 1) } // Text file
        });
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000001, "Test text")));

        // Act
        Result<ExportSummary> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalTextFiles.Should().Be(1);
        result.Value.OutputPath.Should().Be(outputPath);

        _mockHandler.Received(1).Close(0);
    }

    [Fact]
    public void ExportAllTexts_DatFileOpenFails_ShouldReturnFailure()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");

        Error error = new Error(
            "DatFile.CannotOpen",
            "Cannot open",
            ErrorType.IoError);

        _mockHandler.Open(datPath).Returns(Result.Failure<int>(error));

        // Act
        Result<ExportSummary> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DatFile.CannotOpen");
    }

    [Fact]
    public void ExportAllTexts_NullDatPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _exporter.ExportAllTexts(null!, "output.txt");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ExportAllTexts_NullOutputPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _exporter.ExportAllTexts("input.dat", null!);
        act.Should().Throw<ArgumentException>();
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
            { 0x25000001, (100, 1) }, // Text file
            { 0x10000001, (200, 1) }, // Non-text file
            { 0x25000002, (100, 1) }  // Another text file
        });
        _mockHandler.GetSubfileData(Arg.Any<int>(), 0x25000001, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000001, "Text1")));
        _mockHandler.GetSubfileData(Arg.Any<int>(), 0x25000002, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000002, "Text2")));

        // Act
        Result<ExportSummary> result = _exporter.ExportAllTexts(datPath, outputPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalTextFiles.Should().Be(2);

        // Non-text file should not be read - verify by checking received calls count
        _mockHandler.Received(2).GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public void ExportAllTexts_WithProgressCallback_ShouldReportProgress()
    {
        // Arrange
        string datPath = CreateTempFile("test.dat");
        string outputPath = Path.Combine(_tempDir, "output.txt");
        List<(int Processed, int Total)> progressReports = new List<(int Processed, int Total)>();

        // Create 600 files to trigger progress (interval is 500)
        Dictionary<int, (int, int)> fileSizes = Enumerable.Range(1, 600)
            .ToDictionary(
                i => 0x25000000 + i,
                i => (100, 1));

        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(Arg.Any<int>()).Returns(fileSizes);
        _mockHandler.GetSubfileData(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(x => Result.Success(CreateTextSubFileData((int)x[1], "Test")));

        // Act
        Result<ExportSummary> result = _exporter.ExportAllTexts(datPath, outputPath,
            (processed, total) => progressReports.Add((processed, total)));

        // Assert
        result.IsSuccess.Should().BeTrue();
        progressReports.Should().NotBeEmpty();
        progressReports[0].Processed.Should().Be(500);
        progressReports[0].Total.Should().Be(600);
    }

    [Fact]
    public void Constructor_NullHandler_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Exporter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private string CreateTempFile(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static byte[] CreateTextSubFileData(int fileId, string text)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)1); // numFragments (varlen)

        // Fragment
        writer.Write((ulong)1); // fragmentId
        writer.Write(1); // numPieces
        writer.Write((byte)text.Length); // piece length (varlen)
        writer.Write(Encoding.Unicode.GetBytes(text));
        writer.Write(0); // numArgRefs
        writer.Write((byte)0); // numArgStringGroups

        return stream.ToArray();
    }
}
