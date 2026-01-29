using System.Text;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Integration.Features;

public class PatcherTests : IDisposable
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

        var translations = new List<Translation>
        {
            new() { FileId = 0x25000001, GossipId = 1, Content = "Translated" }
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000001, fragmentId: 1)));
        _mockHandler.PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1)
            .Returns(Result.Success());

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.AppliedTranslations.Should().Be(1);
        result.Value.TotalTranslations.Should().Be(1);
        result.Value.SkippedTranslations.Should().Be(0);

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
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.NoTranslations");
    }

    [Fact]
    public void ApplyTranslations_TranslationParseError_ShouldReturnFailure()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        var error = new Error(
            "Translation.FileNotFound",
            "File not found",
            ErrorType.NotFound);

        _mockParser.ParseFile(translationsPath).Returns(Result.Failure<IReadOnlyList<Translation>>(error));

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ApplyTranslations_DatOpenFails_ShouldReturnFailure()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        var translations = new List<Translation>
        {
            new() { FileId = 0x25000001, GossipId = 1, Content = "Test" }
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));

        var error = new Error(
            "DatFile.CannotOpen",
            "Cannot open",
            ErrorType.IoError);

        _mockHandler.Open(datPath).Returns(Result.Failure<int>(error));

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DatFile.CannotOpen");
    }

    [Fact]
    public void ApplyTranslations_FileNotInDat_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        var translations = new List<Translation>
        {
            new() { FileId = 0x25000001, GossipId = 1, Content = "Test" }
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>()); // Empty

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.SkippedTranslations.Should().Be(1);
        result.Value.Warnings.Should().Contain(w => w.Contains("not found"));
    }

    [Fact]
    public void ApplyTranslations_NonTextFile_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        var translations = new List<Translation>
        {
            new() { FileId = 0x10000001, GossipId = 1, Content = "Test" } // Non-text file
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x10000001, (100, 1) }
        });

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.SkippedTranslations.Should().Be(1);
        result.Value.Warnings.Should().Contain(w => w.Contains("not a text file"));
    }

    [Fact]
    public void ApplyTranslations_FragmentNotFound_ShouldSkipWithWarning()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        var translations = new List<Translation>
        {
            new() { FileId = 0x25000001, GossipId = 999, Content = "Test" } // Fragment doesn't exist
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000001, fragmentId: 1))); // Fragment ID 1, not 999

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.SkippedTranslations.Should().Be(1);
        result.Value.Warnings.Should().Contain(w => w.Contains("Fragment 999 not found"));
    }

    [Fact]
    public void ApplyTranslations_BatchesByFileId_ShouldOptimizeIo()
    {
        // Arrange
        string translationsPath = CreateTempFile("translations.txt");
        string datPath = CreateTempFile("test.dat");

        // Same file ID, multiple fragments - should only load file once
        var translations = new List<Translation>
        {
            new() { FileId = 0x25000001, GossipId = 1, Content = "Trans1" },
            new() { FileId = 0x25000001, GossipId = 2, Content = "Trans2" },
            new() { FileId = 0x25000001, GossipId = 3, Content = "Trans3" }
        };

        _mockParser.ParseFile(translationsPath)
            .Returns(Result.Success<IReadOnlyList<Translation>>(translations));
        _mockHandler.Open(datPath).Returns(Result.Success(0));
        _mockHandler.GetAllSubfileSizes(0).Returns(new Dictionary<int, (int, int)>
        {
            { 0x25000001, (100, 1) }
        });
        _mockHandler.GetSubfileVersion(0, 0x25000001).Returns(1);
        _mockHandler.GetSubfileData(0, 0x25000001, 100)
            .Returns(Result.Success(CreateTextSubFileData(0x25000001, fragmentId: 1, fragmentCount: 3)));
        _mockHandler.PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1)
            .Returns(Result.Success());

        // Act
        var result = _patcher.ApplyTranslations(translationsPath, datPath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.AppliedTranslations.Should().Be(3);

        // Should only load the subfile once
        _mockHandler.Received(1).GetSubfileData(0, 0x25000001, 100);
        // Should only save the subfile once
        _mockHandler.Received(1).PutSubfileData(0, 0x25000001, Arg.Any<byte[]>(), 1, 1);
    }

    [Fact]
    public void Constructor_NullHandler_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Patcher(null!, _mockParser);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullParser_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new Patcher(_mockHandler, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private string CreateTempFile(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static byte[] CreateTextSubFileData(int fileId, ulong fragmentId = 1, int fragmentCount = 1)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(fileId);
        writer.Write(new byte[4]); // Unknown1
        writer.Write((byte)0); // Unknown2
        writer.Write((byte)fragmentCount); // numFragments (varlen)

        for (int i = 0; i < fragmentCount; i++)
        {
            writer.Write(fragmentId + (ulong)i); // fragmentId
            writer.Write(1); // numPieces
            writer.Write((byte)4); // piece length (varlen)
            writer.Write(Encoding.Unicode.GetBytes("Test"));
            writer.Write(0); // numArgRefs
            writer.Write((byte)0); // numArgStringGroups
        }

        return stream.ToArray();
    }
}
