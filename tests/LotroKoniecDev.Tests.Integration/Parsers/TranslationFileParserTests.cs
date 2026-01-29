using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Application.Parsers;

namespace LotroKoniecDev.Tests.Integration.Parsers;

public class TranslationFileParserTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TranslationFileParser _parser;

    public TranslationFileParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LotroTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _parser = new TranslationFileParser();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_ValidFile_ShouldReturnTranslations()
    {
        // Arrange
        string filePath = CreateTranslationFile(
            "100||200||Hello World||NULL||NULL||1",
            "100||201||Goodbye||NULL||NULL||1",
            "101||100||Test||1-2||1-2||1"
        );

        // Act
        var result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public void ParseFile_WithComments_ShouldSkipComments()
    {
        // Arrange
        string filePath = CreateTranslationFile(
            "# This is a comment",
            "100||200||Hello||NULL||NULL||1",
            "# Another comment",
            "100||201||World||NULL||NULL||1"
        );

        // Act
        var result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public void ParseFile_WithEmptyLines_ShouldSkipEmptyLines()
    {
        // Arrange
        string filePath = CreateTranslationFile(
            "100||200||Hello||NULL||NULL||1",
            "",
            "   ",
            "100||201||World||NULL||NULL||1"
        );

        // Act
        var result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public void ParseFile_NonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_tempDir, "non_existent.txt");

        // Act
        var result = _parser.ParseFile(nonExistentPath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void ParseFile_ShouldSortByFileIdThenGossipId()
    {
        // Arrange
        string filePath = CreateTranslationFile(
            "200||100||Third||NULL||NULL||1",
            "100||200||Second||NULL||NULL||1",
            "100||100||First||NULL||NULL||1"
        );

        // Act
        var result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var translations = result.Value.ToList();
        translations[0].FileId.Should().Be(100);
        translations[0].GossipId.Should().Be(100);
        translations[1].FileId.Should().Be(100);
        translations[1].GossipId.Should().Be(200);
        translations[2].FileId.Should().Be(200);
    }

    [Fact]
    public void ParseLine_ValidLine_ShouldParseCorrectly()
    {
        // Arrange
        const string line = "12345||67890||Hello World||1-2-3||1-2-3||1";

        // Act
        var result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.FileId.Should().Be(12345);
        result.Value.GossipId.Should().Be(67890);
        result.Value.Content.Should().Be("Hello World");
        result.Value.ArgsOrder.Should().BeEquivalentTo(new[] { 0, 1, 2 });
        result.Value.ArgsId.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public void ParseLine_WithNullArgs_ShouldHaveNullArrays()
    {
        // Arrange
        const string line = "100||200||Test||NULL||NULL||1";

        // Act
        var result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.ArgsOrder.Should().BeNull();
        result.Value.ArgsId.Should().BeNull();
    }

    [Fact]
    public void ParseLine_WithEscapedNewlines_ShouldUnescape()
    {
        // Arrange
        const string line = @"100||200||Line1\r\nLine2||NULL||NULL||1";

        // Act
        var result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Content.Should().Be("Line1\r\nLine2");
    }

    [Fact]
    public void ParseLine_TooFewFields_ShouldReturnFailure()
    {
        // Arrange
        const string line = "100||200||Content";

        // Act
        var result = _parser.ParseLine(line);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void ParseLine_EmptyLine_ShouldReturnFailure()
    {
        // Act
        var result = _parser.ParseLine("");

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_InvalidFileId_ShouldReturnFailure()
    {
        // Arrange
        const string line = "not_a_number||200||Content||NULL||NULL||1";

        // Act
        var result = _parser.ParseLine(line);

        // Assert
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ParseFile_NullPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _parser.ParseFile(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseFile_EmptyPath_ShouldThrow()
    {
        // Act & Assert
        Action act = () => _parser.ParseFile("");
        act.Should().Throw<ArgumentException>();
    }

    private string CreateTranslationFile(params string[] lines)
    {
        string filePath = Path.Combine(_tempDir, $"translations_{Guid.NewGuid():N}.txt");
        File.WriteAllLines(filePath, lines);
        return filePath;
    }
}
