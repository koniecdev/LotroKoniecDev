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
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ParseFile_EmptyPath_ShouldThrowArgumentException()
    {
        // Act
        Func<Result<IReadOnlyList<Translation>>> action = () => _parser.ParseFile("   ");

        // Assert
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ParseFile_NonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_tempDirectory, "nonexistent.txt");

        // Act
        Result<IReadOnlyList<Translation>> result = _parser.ParseFile(nonExistentPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.FileNotFound");
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
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
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
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
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
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].FileId.ShouldBe(12345);
        result.Value[0].GossipId.ShouldBe(67890);
        result.Value[0].Content.ShouldBe("Hello World");
        result.Value[0].ArgsOrder.ShouldBeNull();
        result.Value[0].ArgsId.ShouldBeNull();
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
        result.IsSuccess.ShouldBeTrue();
        result.Value[0].ArgsOrder.ShouldBe(new[] { 0, 1, 2 }); // 1-indexed to 0-indexed
        result.Value[0].ArgsId.ShouldBe(new[] { 3, 4, 5 });
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
        result.IsSuccess.ShouldBeTrue();
        result.Value[0].Content.ShouldBe("Line1\nLine2\rLine3");
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
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(3);
        result.Value[0].FileId.ShouldBe(100);
        result.Value[0].GossipId.ShouldBe(200);
        result.Value[1].FileId.ShouldBe(100);
        result.Value[1].GossipId.ShouldBe(300);
        result.Value[2].FileId.ShouldBe(200);
        result.Value[2].GossipId.ShouldBe(300);
    }

    [Fact]
    public void ParseLine_EmptyLine_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.InvalidFormat");
    }

    [Fact]
    public void ParseLine_InsufficientFields_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Content");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.InvalidFormat");
    }

    [Fact]
    public void ParseLine_InvalidFileId_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("not_a_number||200||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Fact]
    public void ParseLine_InvalidGossipId_ShouldReturnFailure()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||not_a_number||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Fact]
    public void ParseLine_ValidLine_ShouldReturnSuccess()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Test content||NULL||NULL||1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.FileId.ShouldBe(100);
        result.Value.GossipId.ShouldBe(200);
        result.Value.Content.ShouldBe("Test content");
    }

    [Fact]
    public void ParseLine_ApprovedField_ShouldParseAsTrue()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Content||NULL||NULL||1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsApproved.ShouldBeTrue();
    }

    [Fact]
    public void ParseLine_UnapprovedField_ShouldParseAsFalse()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Content||NULL||NULL||0");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.IsApproved.ShouldBeFalse();
    }

    [Fact]
    public void ParseLine_FiveFields_ShouldReturnFailure()
    {
        // Act — missing approved field
        Result<Translation> result = _parser.ParseLine("100||200||Content||NULL||NULL");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.InvalidFormat");
    }

    [Fact]
    public void ParseLine_ContentContainsSeparator_ShouldPreserveFullContent()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Left||Right||NULL||NULL||1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.FileId.ShouldBe(100);
        result.Value.GossipId.ShouldBe(200);
        result.Value.Content.ShouldBe("Left||Right");
        result.Value.ArgsOrder.ShouldBeNull();
        result.Value.ArgsId.ShouldBeNull();
        result.Value.IsApproved.ShouldBeTrue();
    }

    [Theory]
    [InlineData("100||200||a||b||NULL||NULL||1", "a||b")]              // separator inside content
    [InlineData("100||200||||trailing||NULL||NULL||1", "||trailing")] // content starts with separator
    [InlineData("100||200||leading||||NULL||NULL||1", "leading||")]   // content ends with separator
    [InlineData("100||200||a||b||c||NULL||NULL||1", "a||b||c")]       // multiple separators
    [InlineData("100||200||||NULL||NULL||1", "")]                     // empty content, minimum field count
    public void ParseLine_ContentWithSeparators_ShouldRecoverExactContent(string line, string expectedContent)
    {
        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(expectedContent);
    }

    [Fact]
    public void ParseLine_ContentContainsSeparatorWithArgs_ShouldKeepContentAndArgsSeparate()
    {
        // Act
        Result<Translation> result = _parser.ParseLine("100||200||Tekst||z argumentem||1-2||3-4||0");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe("Tekst||z argumentem");
        result.Value.ArgsOrder.ShouldBe(new[] { 0, 1 });
        result.Value.ArgsId.ShouldBe(new[] { 2, 3 });
        result.Value.IsApproved.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Left||Right")]
    [InlineData("||leading")]
    [InlineData("trailing||")]
    [InlineData("a||b||c")]
    [InlineData("Witaj||w Srodziemiu||przyjacielu")]
    public void ParseLine_ContentWithSeparators_ShouldRoundTripToIdenticalContent(string content)
    {
        // Arrange — serialize via the canonical export line format (file_id||gossip_id||content||args_order||args_id||approved)
        string serializedLine = $"100||200||{content}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(serializedLine);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
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
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
    }
}
