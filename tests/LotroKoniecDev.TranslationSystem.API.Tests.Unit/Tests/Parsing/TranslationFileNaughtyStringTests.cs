using System.Text;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// Runs the Big List of Naughty Strings through the TMS half of the <c>||</c> contract (#569):
/// <see cref="TranslationFileSerializer"/> and <see cref="TranslationExportParser"/>.
/// </summary>
public sealed class TranslationFileNaughtyStringTests
{
    private readonly TranslationFileSerializer _serializer = new();
    private readonly TranslationExportParser _parser = new();

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_NaughtyContent_ShouldRoundTripExactly(string naughty)
    {
        // Arrange
        string file = _serializer.Serialize([Row(620756992, 1001, naughty, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(naughty);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_NaughtyContentCarryingTheFieldSeparator_ShouldRecoverExactContent(string naughty)
    {
        // Arrange: the separator is legal inside content; the parser anchors from both ends.
        string content = $"{naughty}||{naughty}";
        string file = _serializer.Serialize([Row(620756992, 1001, content, "1-2", "1-2")]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        ParsedExportRow row = parsed.Rows.ShouldHaveSingleItem();
        row.Content.ShouldBe(content);
        row.ArgsOrder.ShouldBe("1-2");
        row.ArgsId.ShouldBe("1-2");
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_NaughtyContentOpeningWithTheCommentMarker_ShouldStillParse(string naughty)
    {
        // Arrange: '#' starts a comment only at the start of a LINE; a content field never is one.
        string content = $"#{naughty}";
        string file = _serializer.Serialize([Row(620756992, 1001, content, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_ManyNaughtyRows_ShouldKeepEveryRowOnItsOwnLine(string naughty)
    {
        // Arrange: a terminator error in one row swallows its neighbours, not just itself.
        List<ArtifactRow> rows =
        [
            Row(620756992, 1001, naughty, null, null),
            Row(620756992, 1002, $"{naughty}||{naughty}", "1", "1"),
            Row(620756993, 1003, $"#{naughty}", null, null)
        ];
        string file = _serializer.Serialize(rows);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.Select(row => row.Content).ShouldBe([naughty, $"{naughty}||{naughty}", $"#{naughty}"]);
    }

    [Theory]
    [InlineData("Polish\ntext")]
    [InlineData("Polish\r\ntext")]
    [InlineData("Polish\rtext")]
    [InlineData("Multi\nline\r\nPolish\rtext")]
    public async Task SerializeThenParse_ContentCarryingARealNewline_ShouldRoundTripExactly(string content)
    {
        // Arrange: Polish from the multi-line editor reaches the serializer raw, so the writer escapes
        // it (ADR-0039, #596).
        string file = _serializer.Serialize([Row(620756992, 1001, content, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(content);
    }

    [Theory]
    [InlineData(@"C:\notes")]
    [InlineData(@"backslash \n here")]
    [InlineData(@"\r")]
    [InlineData(@"\\n")]
    public async Task SerializeThenParse_ContentCarryingALiteralBackslashEscapeSequence_ShouldRoundTripExactly(string content)
    {
        // Arrange: the escape character is escaped too, so a backslash before 'r' or 'n' survives (#596).
        string file = _serializer.Serialize([Row(620756992, 1001, content, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(content);
    }

    [Theory]
    [InlineData("abc|")]
    [InlineData("abc||")]
    [InlineData("abc|||")]
    [InlineData("|")]
    [InlineData("||")]
    [InlineData("|||")]
    public async Task SerializeThenParse_ContentEndingInAnOddNumberOfPipes_ShouldRoundTripExactly(string content)
    {
        // Arrange: scanning backwards, the last two pipes of a run are the separator (ADR-0042, #597).
        // No naughty string ends in an odd number of pipes, so this case is built by hand.
        string file = _serializer.Serialize([Row(620756992, 1001, content, "1-2", "3-4")]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        ParsedExportRow row = parsed.Rows.ShouldHaveSingleItem();
        row.Content.ShouldBe(content);
        row.ArgsOrder.ShouldBe("1-2");
        row.ArgsId.ShouldBe("3-4");
    }

    [Theory]
    [InlineData("620756992||1001||Content||1-x||NULL||1", "args_order")]
    [InlineData("620756992||1001||Content||NULL||1-x||1", "args_id")]
    [InlineData("620756992||1001||Content||1--2||NULL||1", "args_order")]
    [InlineData("620756992||1001||Content||ONE||NULL||1", "args_order")]
    public async Task ParseAsync_MalformedArgsColumn_ShouldRejectTheRowInsteadOfStoringItVerbatim(string line, string column)
    {
        // Act: stored as it is, the value would be unusable yet still take part in the diff (#597).
        ParsedExport parsed = await ParseAsync($"{line}\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain(column);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.PipeRuns), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_EveryPipeRunUpToSixCharacters_ShouldRoundTripExactly(string content)
    {
        // Arrange: exhaustive over the alphabet this format is weakest at, at either boundary or both.
        string file = _serializer.Serialize([Row(620756992, 1001, content, "1-2", "3-4")]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        ParsedExportRow row = parsed.Rows.ShouldHaveSingleItem();
        row.Content.ShouldBe(content);
        row.ArgsOrder.ShouldBe("1-2");
        row.ArgsId.ShouldBe("3-4");
    }

    [Fact]
    public async Task SerializeThenParse_EmptyContent_ShouldRoundTripAsEmpty()
    {
        // Arrange: legal game content, and the naughty list has no empty entry.
        string file = _serializer.Serialize([Row(620756992, 1001, string.Empty, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(string.Empty);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public async Task ParseAsync_NonAsciiDigitsInTheFileIdColumn_ShouldRejectTheRow(string naughtyDigits)
    {
        // Act: accepting a fullwidth or Arabic-Indic "number" would attach a row to the wrong fragment.
        ParsedExport parsed = await ParseAsync($"{naughtyDigits}||1001||Content||NULL||NULL||1\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain("not a valid integer");
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public async Task ParseAsync_NonAsciiDigitsInTheGossipIdColumn_ShouldRejectTheRow(string naughtyDigits)
    {
        // Act
        ParsedExport parsed = await ParseAsync($"620756992||{naughtyDigits}||Content||NULL||NULL||1\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain("not a valid integer");
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task ParseAsync_NaughtyStringInEveryColumn_ShouldAnswerWithErrorsInsteadOfThrowing(string naughty)
    {
        // Arrange: the whole uploaded line is hostile: both id columns, the content and both args.
        string line = string.Join("||", naughty, naughty, naughty, naughty, naughty, naughty);

        // Act & Assert: a parse error is reported to the admin; an exception would abort the import.
        await Should.NotThrowAsync(() => ParseAsync(line));
    }

    /// <summary>
    /// The digest of the row's own triple, because the reader verifies the column (ADR-0047) and a random
    /// one would reject every row. A real row carries the English digest; the projector's tests pin that.
    /// </summary>
    private static ArtifactRow Row(int fileId, long gossipId, string content, string? argsOrder, string? argsId)
        => new(
            fileId,
            gossipId,
            content,
            argsOrder,
            argsId,
            SourceHash.Compute(content, argsOrder, argsId).ToWireDigest());

    private async Task<ParsedExport> ParseAsync(string file)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(file));

        return await _parser.ParseAsync(stream, CancellationToken.None);
    }
}
