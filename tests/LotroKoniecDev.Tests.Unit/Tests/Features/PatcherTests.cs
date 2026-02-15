using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Tests.Unit.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class PatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDatFileHandler _mockHandler;
    private readonly ITranslationParser _mockParser;
    private readonly Patcher _patcher;

    public PatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LotroPatcherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _mockHandler = Substitute.For<IDatFileHandler>();
        _mockParser = Substitute.For<ITranslationParser>();
        _patcher = new Patcher(_mockHandler, _mockParser);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyTranslations_SuccessfulPatch_ShouldReturnSummary()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 1, Content = "Translated" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, 1, 1)));
        _mockHandler.PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1)
            .Returns(Result.Success());

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);
        result.Value.TotalTranslations.ShouldBe(1);
        result.Value.SkippedTranslations.ShouldBe(0);

        _mockHandler.Received(1).Flush(0);
        _mockHandler.Received(1).Close(0);
    }

    [Fact]
    public void ApplyTranslations_NoTranslations_ShouldReturnFailure()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(new List<Translation>()));

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.NoTranslations");
    }

    [Fact]
    public void ApplyTranslations_TranslationParseError_ShouldReturnFailure()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        Error error = new("Translation.FileNotFound", "File not found", ErrorType.NotFound);
        _mockParser.ParseFile(translationsPath).Returns(Result.Failure<IReadOnlyList<Translation>>(error));

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ApplyTranslations_DatOpenFails_ShouldReturnFailure()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 1, Content = "Test" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));

        Error error = new("DatFile.CannotOpen", "Cannot open", ErrorType.IoError);
        _mockHandler.Open(datPath).Returns(Result.Failure<int>(error));

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpen");
    }

    [Fact]
    public void ApplyTranslations_FileNotInDat_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 1, Content = "Test" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>());

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("not found"));
    }

    [Fact]
    public void ApplyTranslations_NonTextFile_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x10000001, GossipId = 1, Content = "Test" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x10000001, (100, 1) }
        });

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("not a text file"));
    }

    [Fact]
    public void ApplyTranslations_FragmentNotFound_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 999, Content = "Test" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, 1, 1)));

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
        result.Value.Warnings.ShouldContain(w => w.Contains("Fragment 999 not found"));
    }

    [Fact]
    public void ApplyTranslations_BatchesByFileId_ShouldOptimizeIo()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 1, Content = "Trans1" },
            new() { FileId = 0x25000001, GossipId = 2, Content = "Trans2" },
            new() { FileId = 0x25000001, GossipId = 3, Content = "Trans3" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, 1, 3)));
        _mockHandler.PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1)
            .Returns(Result.Success());

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(3);
        _mockHandler.Received(1).GetSubfileData(0, 0x25000001, 100);
        _mockHandler.Received(1).PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1);
    }

    [Fact]
    public void ApplyTranslations_WithSeparator_ShouldSplitIntoPieces()
    {
        // Arrange — translation with <--DO_NOT_TOUCH!--> placeholder
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new()
            {
                FileId = 0x25000001,
                GossipId = 1,
                Content = "Witaj<--DO_NOT_TOUCH!-->w Srodziemiu"
            }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(TestDataFactory.CreateTextSubFileData(0x25000001, 1, 1)));
        _mockHandler.PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1)
            .Returns(Result.Success());

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert — verify the binary written back contains 2 pieces
        result.IsSuccess.ShouldBeTrue();
        result.Value.AppliedTranslations.ShouldBe(1);

        _mockHandler.Received(1).PutSubfileData(0, 0x25000001,
            Arg.Is<byte[]>(data => VerifyPatchedPieces(data, "Witaj", "w Srodziemiu")),
            1, 1);
    }

    [Fact]
    public void ApplyTranslations_LoadSubFileFails_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        List<Translation> translations =
        [
            new() { FileId = 0x25000001, GossipId = 1, Content = "Test" }
        ];

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);

        Error readError = new("SubFile.ParseError", "Corrupted data", Primitives.Enums.ErrorType.IoError);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Failure<byte[]>(readError));

        // Act
        Result<PatchSummary> result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedTranslations.ShouldBe(1);
    }

    [Fact]
    public void Constructor_NullHandler_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Patcher(null!, _mockParser);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullParser_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Patcher(_mockHandler, null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    private string CreateTempFile(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    /// <summary>
    /// Re-parses serialized SubFile data and checks that the fragment's pieces match expected values.
    /// </summary>
    private static bool VerifyPatchedPieces(byte[] data, params string[] expectedPieces)
    {
        SubFile subFile = new();
        subFile.Parse(data);
        Fragment fragment = subFile.Fragments.Values.First();
        return fragment.Pieces.SequenceEqual(expectedPieces);
    }
}
