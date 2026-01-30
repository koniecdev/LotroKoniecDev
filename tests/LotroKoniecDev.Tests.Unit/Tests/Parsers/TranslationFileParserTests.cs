using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.Unit.Tests.Parsers;

public sealed class TranslationFileParserTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly TranslationFileParser _parser;

    public TranslationFileParserTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"LotroTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        _parser = new TranslationFileParser();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_NullPath_ShouldThrowArgumentException()
    {
        // Act
        Func<Result<IReadOnlyList<Translation>>> action = () => _parser.ParseFile(null!);

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseFile_EmptyPath_ShouldThrowArgumentException()
    {
        // Act
        Func<Result<IReadOnlyList<Translation>>> action = () => _parser.ParseFile("   ");

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseFile_NonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_tempDirectory, "nonexistent.txt");

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(nonExistentPath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.FileNotFound");
    }

    [Fact]
    public void ParseFile_EmptyFile_ShouldReturnEmptyList()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "empty.txt");
        File.WriteAllText(filePath, "");

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public void ParseFile_WithComments_ShouldSkipCommentLines()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "comments.txt");
        string content = """
                         # This is a comment
                         100||200||Test content||NULL||NULL||1
                         # Another comment
                           # Comment with leading spaces
                         """;
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    [Fact]
    public void ParseFile_ValidLine_ShouldParseCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "valid.txt");
        string content = "12345||67890||Hello World||NULL||NULL||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].FileId.Should().Be(12345);
        result.Value[0].GossipId.Should().Be(67890);
        result.Value[0].Content.Should().Be("Hello World");
        result.Value[0].ArgsOrder.Should().BeNull();
        result.Value[0].ArgsId.Should().BeNull();
    }

    [Fact]
    public void ParseFile_WithArgsOrder_ShouldParseArgsCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "args.txt");
        string content = "100||200||Content||1-2-3||4-5-6||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value[0].ArgsOrder.Should().BeEquivalentTo([0, 1, 2]); // 1-indexed to 0-indexed
        result.Value[0].ArgsId.Should().BeEquivalentTo([3, 4, 5]);
    }

    [Fact]
    public void ParseFile_WithEscapedNewlines_ShouldUnescapeCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "escaped.txt");
        string content = @"100||200||Line1\nLine2\rLine3||NULL||NULL||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value[0].Content.Should().Be("Line1\nLine2\rLine3");
    }

    [Fact]
    public void ParseFile_MultipleLines_ShouldSortByFileIdAndGossipId()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "multiple.txt");
        string content = """
                         200||300||Third||NULL||NULL||1
                         100||200||First||NULL||NULL||1
                         100||300||Second||NULL||NULL||1
                         """;
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value[0].FileId.Should().Be(100);
        result.Value[0].GossipId.Should().Be(200);
        result.Value[1].FileId.Should().Be(100);
        result.Value[1].GossipId.Should().Be(300);
        result.Value[2].FileId.Should().Be(200);
        result.Value[2].GossipId.Should().Be(300);
    }

    [Fact]
    public void ParseLine_EmptyLine_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.InvalidFormat");
    }

    [Fact]
    public void ParseLine_InsufficientFields_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Content");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.InvalidFormat");
    }

    [Fact]
    public void ParseLine_InvalidFileId_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("not_a_number||200||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.ParseError");
    }

    [Fact]
    public void ParseLine_InvalidGossipId_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||not_a_number||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Translation.ParseError");
    }

    [Fact]
    public void ParseLine_ValidLine_ShouldReturnSuccess()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Test content||NULL||NULL||1");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FileId.Should().Be(100);
        result.Value.GossipId.Should().Be(200);
        result.Value.Content.Should().Be("Test content");
    }

    [Fact]
    public void ParseFile_WithInvalidLines_ShouldSkipAndContinue()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "mixed.txt");
        string content = """
                         100||200||Valid line||NULL||NULL||1
                         invalid||line||missing||fields
                         300||400||Another valid||NULL||NULL||1
                         """;
        File.WriteAllText(filePath, content);

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }
}
