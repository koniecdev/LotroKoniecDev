using System.Text;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// Hostile-input coverage for the TMS half of the <c>||</c> contract (#569): the Big List of Naughty
/// Strings driven through the producer/consumer pair the TMS owns — <see cref="TranslationFileSerializer"/>
/// writing the distributed file and <see cref="TranslationExportParser"/> reading an uploaded
/// <c>exported.txt</c> back.
/// </summary>
/// <remarks>
/// Changing the format itself needs an ADR plus updated golden fixtures on both sides of the
/// contract (CLAUDE.md) — ADR-0039 is the last one.
/// </remarks>
public sealed class TranslationFileNaughtyStringTests
{
    private readonly TranslationFileSerializer _serializer = new();
    private readonly TranslationExportParser _parser = new();

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_NaughtyContent_ShouldRoundTripExactly(string naughty)
    {
        // Arrange
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, naughty, null, null)]);

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
        // Arrange — the separator is legal inside content; the parser anchors from both ends.
        string content = $"{naughty}||{naughty}";
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, "1-2", "1-2")]);

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
        // Arrange — '#' starts a comment only at the start of a LINE; a content field never is one.
        string content = $"#{naughty}";
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, null, null)]);

        // Act
        ParsedExport parsed = await ParseAsync(file);

        // Assert
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_ManyNaughtyRows_ShouldKeepEveryRowOnItsOwnLine(string naughty)
    {
        // Arrange — a real artifact is one long file; a length or terminator error in one row
        // swallows its neighbours rather than just itself.
        List<ArtifactRow> rows =
        [
            new(620756992, 1001, naughty, null, null),
            new(620756992, 1002, $"{naughty}||{naughty}", "1", "1"),
            new(620756993, 1003, $"#{naughty}", null, null)
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
        // Arrange — translator-submitted Polish reaches the serializer raw (the editor is a
        // multi-line textarea and the upsert slice only checks NotEmpty), so the WRITER escapes it
        // (ADR-0039). Before that, a newline split one row into two malformed lines and the fragment
        // silently disappeared from the distributed file (#596).
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, null, null)]);

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
        // Arrange — the other half of #596: the escape escapes its own escape character, so a
        // backslash in front of 'r'/'n' survives instead of unfolding into a control character.
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, null, null)]);

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
        // Arrange — the trailing boundary is found by scanning BACKWARD (ADR-0042), so the last two
        // pipes of a run are the separator and every earlier one stays content. Split resolved the
        // boundary greedily left to right, so the last pipe was swallowed and reappeared glued to
        // the args column — identically in both parsers, which is exactly why the parity guard could
        // not see it (#597). No entry of the naughty list ends in an odd pipe run, so this collision
        // has to be composed by hand.
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, "1-2", "3-4")]);

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
        // Act — a value that is neither NULL nor "1-2-3" used to be stored as-is, where it was
        // unusable as an order and still took part in the import diff (#597, spec 0001).
        ParsedExport parsed = await ParseAsync($"{line}\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain(column);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.PipeRuns), MemberType = typeof(NaughtyStringCases))]
    public async Task SerializeThenParse_EveryPipeRunUpToSixCharacters_ShouldRoundTripExactly(string content)
    {
        // Arrange — exhaustive over the alphabet this format is weakest at, in BOTH parities: the
        // run can sit at the leading boundary, the trailing one, or both at once. The hand-picked
        // cases above only sample it.
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, content, "1-2", "3-4")]);

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
        // Arrange — an empty fragment is legal game content and must survive; the naughty list has
        // no empty entry (its shortest is one character), so this case is composed by hand.
        string file = _serializer.Serialize([new ArtifactRow(620756992, 1001, string.Empty, null, null)]);

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
        // Act — a fullwidth or Arabic-Indic "number" addresses no real DAT subfile; accepting it
        // would attach an import row to the wrong fragment.
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
        // Arrange — the whole uploaded line is hostile: both id columns, the content and both args.
        string line = string.Join("||", naughty, naughty, naughty, naughty, naughty, naughty);

        // Act & Assert — a per-line parse error is a legitimate outcome (it is reported back to the
        // admin); an escaping exception is not, because it would abort the whole import.
        await Should.NotThrowAsync(() => ParseAsync(line));
    }

    private async Task<ParsedExport> ParseAsync(string file)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(file));

        return await _parser.ParseAsync(stream, CancellationToken.None);
    }
}
