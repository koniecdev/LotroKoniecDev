using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Exporting;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
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
        IProgress<OperationProgress> progress = Substitute.For<IProgress<OperationProgress>>();
        _sut = new ExportTextsQueryHandler(_mockHandler, progress);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "output.txt")]
    [InlineData("test.dat", "")]
    public async Task Handle_EmptyPath_ShouldReturnValidationFailure(string datFilePath, string outputPath)
    {
        // Arrange
        ExportTextsQuery query = new(datFilePath, outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe("ExportTextsQuery.Validation");
        _mockHandler.DidNotReceive().Open(Arg.Any<string>(), Arg.Any<DatFileAccess>());
    }

    [Fact]
    public async Task Handle_SuccessfulExport_ShouldReturnSummary()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open("test.dat", DatFileAccess.Read).Returns(Result.Success(0));
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
        _mockHandler.Open("test.dat", DatFileAccess.Read).Returns(Result.Failure<int>(error));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
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

        _mockHandler.Open("test.dat", DatFileAccess.Read).Returns(Result.Success(0));
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
    public async Task Handle_GetSubfileDataFails_ShouldContinueWithOtherFiles()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");
        Error loadError = new("SubFile.ParseError", "Corrupted", ErrorType.IoError);

        _mockHandler.Open("test.dat", DatFileAccess.Read).Returns(Result.Success(0));
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

        // Assert: should succeed with partial results, not fail entirely
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalTextFiles.ShouldBe(2);
        result.Value.TotalFragments.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_ExceptionDuringExport_ShouldStillCloseDatFile()
    {
        // Arrange
        string outputPath = Path.Combine(_tempDir, "output.txt");

        _mockHandler.Open("test.dat", DatFileAccess.Read).Returns(Result.Success(42));
        _mockHandler.GetAllSubfileSizes(42)
            .Throws(new InvalidOperationException("Simulated failure"));

        ExportTextsQuery query = new("test.dat", outputPath);

        // Act
        Result<ExportSummaryResponse> result = await _sut.Handle(query, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        _mockHandler.Received(1).Close(42);
    }

    [Theory]
    [InlineData("plain", "620756992||1001||plain||NULL||NULL||1")]
    [InlineData("Line1\nLine2", @"620756992||1001||Line1\nLine2||NULL||NULL||1")]
    [InlineData("Line1\r\nLine2", @"620756992||1001||Line1\r\nLine2||NULL||NULL||1")]
    [InlineData(@"C:\notes", @"620756992||1001||C:\\notes||NULL||NULL||1")]
    public void FormatRow_WithEscapableText_ShouldFoldItOntoOneLine(string text, string expected)
        // The six columns are unchanged; ADR-0047 appends the source digest of the very triple the
        // row carries, so the expectation is composed rather than restated in every InlineData.
        => ExportTextsQueryHandler.FormatRow(620756992, 1001, text, "NULL", "NULL", argumentCount: 0)
            .ShouldBe($"{expected}||{SourceDigest.ForExportForm(text, 0)}");

    [Theory]
    [InlineData(0, "NULL", "NULL")]
    [InlineData(2, "1-2", "1-2")]
    public void FormatRow_ShouldEndInTheDigestOfItsOwnExportedTriple(int argumentCount, string argsOrder, string argsId)
    {
        // ADR-0047 §2: exported.txt carries the digest too, so a hand-edited export stays patchable
        // and the TMS import can verify the column instead of shipping an artifact players reject.
        string row = ExportTextsQueryHandler.FormatRow(620756992, 1001, "Some text", argsOrder, argsId, argumentCount);

        row.ShouldEndWith($"||{SourceDigest.ForExportForm("Some text", argumentCount)}");
    }

    [Fact]
    public void FormatRow_ThenParse_ShouldCarryTheDigestTheWriteGuardChecks()
    {
        // The digest is useless unless it reaches the parsed row, because that is the value the guard
        // compares the fragment against.
        string row = ExportTextsQueryHandler.FormatRow(620756992, 1001, "Some text", "NULL", "NULL", argumentCount: 0);

        new TranslationFileParser().ParseLine(row).Value.SourceDigest
            .ShouldBe(SourceDigest.ForExportForm("Some text", 0));
    }

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData(@"C:\notes")]
    [InlineData("Tekst z <--DO_NOT_TOUCH!--> argumentem")]
    [InlineData(@"a||b" + "\n" + "c")]
    public void FormatRow_ThenParse_ShouldRecoverTheFragmentTextExactly(string text)
    {
        // The export half of the || round trip (ADR-0039): what the exporter writes is what the
        // patcher's own parser reads back. Nothing else pins the handler's escape call.
        string row = ExportTextsQueryHandler.FormatRow(620756992, 1001, text, "NULL", "NULL", argumentCount: 0);

        new TranslationFileParser().ParseLine(row).Value.Content.ShouldBe(text);
    }
}
