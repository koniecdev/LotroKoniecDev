using LotroKoniecDev.Application.Abstractions;
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
        Func<Result<TranslationParseResult>> action = () => _parser.ParseFile(null!);

        // Assert
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ParseFile_EmptyPath_ShouldThrowArgumentException()
    {
        // Act
        Func<Result<TranslationParseResult>> action = () => _parser.ParseFile("   ");

        // Assert
        action.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ParseFile_NonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_tempDirectory, "nonexistent.txt");

        // Act
        Result<TranslationParseResult> result = _parser.ParseFile(nonExistentPath);

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
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.ShouldBeEmpty();
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
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.Count.ShouldBe(1);
    }

    [Fact]
    public void ParseFile_ValidLine_ShouldParseCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "valid.txt");
        string content = "12345||67890||Hello World||NULL||NULL||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.Count.ShouldBe(1);
        result.Value.Translations[0].FileId.ShouldBe(12345);
        result.Value.Translations[0].GossipId.ShouldBe(67890UL);
        result.Value.Translations[0].Content.ShouldBe("Hello World");
        result.Value.Translations[0].ArgsOrder.ShouldBeNull();
        result.Value.Translations[0].ArgsId.ShouldBeNull();
    }

    [Fact]
    public void ParseFile_WithArgsOrder_ShouldParseArgsCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "args.txt");
        string content = "100||200||Content||1-2-3||4-5-6||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations[0].ArgsOrder.ShouldBe(new[] { 0, 1, 2 }); // 1-indexed to 0-indexed
        result.Value.Translations[0].ArgsId.ShouldBe(new[] { 3, 4, 5 });
    }

    [Fact]
    public void ParseFile_WithEscapedNewlines_ShouldUnescapeCorrectly()
    {
        // Arrange
        string filePath = Path.Combine(_tempDirectory, "escaped.txt");
        string content = @"100||200||Line1\nLine2\rLine3||NULL||NULL||1";
        File.WriteAllText(filePath, content);

        // Act
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations[0].Content.ShouldBe("Line1\nLine2\rLine3");
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
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.Count.ShouldBe(3);
        result.Value.Translations[0].FileId.ShouldBe(100);
        result.Value.Translations[0].GossipId.ShouldBe(200UL);
        result.Value.Translations[1].FileId.ShouldBe(100);
        result.Value.Translations[1].GossipId.ShouldBe(300UL);
        result.Value.Translations[2].FileId.ShouldBe(200);
        result.Value.Translations[2].GossipId.ShouldBe(300UL);
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

    [Theory]
    [InlineData("100||2147483647||Content||NULL||NULL||1", 2147483647UL)]                       // int.MaxValue — the old ceiling
    [InlineData("100||2147483648||Content||NULL||NULL||1", 2147483648UL)]                       // int.MaxValue + 1 — previously warn-skipped (silent loss)
    [InlineData("100||9223372036854775808||Content||NULL||NULL||1", 9223372036854775808UL)]     // long.MaxValue + 1 — the high-bit band long cannot hold
    [InlineData("100||18446744073709551615||Content||NULL||NULL||1", 18446744073709551615UL)]   // ulong.MaxValue — full 8-byte range the exporter can write
    public void ParseLine_GossipIdAtOrAboveIntMaxValue_ShouldParseFullUnsignedRange(string line, ulong expectedGossipId)
    {
        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.GossipId.ShouldBe(expectedGossipId);
    }

    [Theory]
    [InlineData("100||-5||Content||NULL||NULL||1")]                          // negative — meaningless for an unsigned 8-byte id
    [InlineData("100||18446744073709551616||Content||NULL||NULL||1")]        // ulong.MaxValue + 1 — beyond the 8-byte range
    public void ParseLine_GossipIdOutsideUnsignedRange_ShouldReturnFailure(string line)
    {
        // Act: the tolerant parser must report a per-line failure (warn-skip), not let the
        // OverflowException abort the whole file.
        Result<Translation> result = _parser.ParseLine(line);

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
        result.Value.GossipId.ShouldBe(200UL);
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
        // Act: missing approved field
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
        result.Value.GossipId.ShouldBe(200UL);
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
        // Arrange: serialize via the canonical export line format (file_id||gossip_id||content||args_order||args_id||approved)
        string serializedLine = $"100||200||{content}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(serializedLine);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Fact]
    public void ParseFile_WithInvalidLines_ShouldSkipContinueAndReportTheRejectedLine()
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
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert: a rejected line is reported, never silently dropped (ADR-0042); the warning
        // travels into PatchSummaryResponse.Warnings, which the CLI prints.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.Count.ShouldBe(2);
        result.Value.Warnings.ShouldHaveSingleItem();
        result.Value.RejectedLineCount.ShouldBe(1);
    }

    [Fact]
    public void ParseFile_WithMoreRejectedLinesThanTheWarningCap_ShouldQuoteTheCapAndCountTheRest()
    {
        // Arrange: 150 malformed rows. Every warning quotes a whole line, and a real polish.txt is
        // ~790k rows, so an uncapped list would bury the console (the TMS import caps for the same
        // reason, spec 0006). The COUNT must stay exact even though the list is truncated.
        string filePath = Path.Combine(_tempDirectory, "all-bad.txt");
        File.WriteAllLines(filePath, Enumerable.Range(0, 150).Select(index => $"100||{index}||Content||1-x||NULL||1"));

        // Act
        Result<TranslationParseResult> result = _parser.ParseFile(filePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Translations.ShouldBeEmpty();
        result.Value.RejectedLineCount.ShouldBe(150);
        result.Value.Warnings.Count.ShouldBe(101);
        result.Value.Warnings[^1].ShouldContain("and 50 more rejected lines");
    }

    [Theory]
    [InlineData("100||200||Content||1-x||NULL||1", "args_order")]
    [InlineData("100||200||Content||NULL||1-x||1", "args_id")]
    [InlineData("100||200||Content||1--2||NULL||1", "args_order")]
    [InlineData("100||200||Content||ONE||NULL||1", "args_order")]
    [InlineData("100||200||Content|| 1-2||NULL||1", "args_order")]
    [InlineData("100||200||Content||99999999999||NULL||1", "args_order")]
    public void ParseLine_MalformedArgsColumn_ShouldFailInsteadOfSwallowingIt(string line, string column)
    {
        // Act: a bare catch used to turn an unparsable args column into null, so a fragment with
        // reordered arguments was patched without its ordering and nobody was told (#597).
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
        result.Error.Message.ShouldContain(column);
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLine_AbsentArgsColumn_ShouldParseAsNoArguments(string absent)
    {
        // Act: every spelling of "this row carries no argument order" stays a success.
        Result<Translation> result = _parser.ParseLine($"100||200||Content||{absent}||{absent}||1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ArgsOrder.ShouldBeNull();
        result.Value.ArgsId.ShouldBeNull();
    }

    [Theory]
    [InlineData("abc|")]
    [InlineData("abc||")]
    [InlineData("abc|||")]
    [InlineData("|")]
    [InlineData("||")]
    [InlineData("|||")]
    [InlineData("|abc|")]
    [InlineData("a|b|")]
    public void ParseLine_ContentEndingInPipes_ShouldKeepEveryPipeAndLeaveTheArgsColumnsClean(string content)
    {
        // Arrange: the boundary is found by scanning backward (ADR-0042), so the last two pipes of
        // a run are the separator and everything before them stays content. Split resolved this
        // greedily left to right and lost the last pipe into the args column (#597).
        string line = $"620756992||1001||{content}||1-2||3-4||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
        result.Value.ArgsOrder.ShouldBe(new[] { 0, 1 });
        result.Value.ArgsId.ShouldBe(new[] { 2, 3 });
    }

    [Fact]
    public void ParseLine_SevenColumnLine_ShouldCarryTheSourceDigest()
    {
        // Act: the digest is the value the write guard compares the fragment against, so it has to
        // reach the parsed row intact (ADR-0047).
        Result<Translation> result = new TranslationFileParser()
            .ParseLine("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||a37cc1683216cd32");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceDigest.ShouldBe("a37cc1683216cd32");
        result.Value.Content.ShouldBe("Witaj w Srodziemiu!");
        result.Value.IsApproved.ShouldBeTrue();
    }

    [Fact]
    public void ParseLine_SixColumnLine_ShouldSucceedWithoutADigestRatherThanRejectTheRow()
    {
        // Act: this matters (ADR-0047 §3). Rejecting here would make a file that is six columns throughout
        // NoTranslationsEveryLineRejected, which the launch path turns into RepatchFailed and
        // refuses to start the game on. The GUARD skips such rows; the parser never does.
        Result<Translation> result = new TranslationFileParser()
            .ParseLine("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SourceDigest.ShouldBeNull();
    }

    [Fact]
    public void ParseLine_SevenColumnLineWhoseContentHoldsTheSeparatorAndTrailingPipes_ShouldCarveBothEnds()
    {
        // Act: the seventh column adds one backward step; the content boundary must not move.
        Result<Translation> result = new TranslationFileParser()
            .ParseLine("620756992||1009||Trzy rury|||||1-2||3-4||1||2f2d1cb2f502250a");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe("Trzy rury|||");
        result.Value.SourceDigest.ShouldBe("2f2d1cb2f502250a");
    }
}
