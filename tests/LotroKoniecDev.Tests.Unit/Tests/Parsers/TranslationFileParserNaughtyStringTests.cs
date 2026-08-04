using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Parsers;

/// <summary>
/// Hostile-input coverage for the patcher's half of the <c>||</c> contract (#569): the Big List of
/// Naughty Strings driven through the exact producer/consumer pair the CLI uses — the escape
/// <c>ExportTextsQueryHandler</c> applies when writing <c>exported.txt</c>, and
/// <see cref="TranslationFileParser.ParseLine"/> reading it back.
/// </summary>
/// <remarks>
/// These tests pin CURRENT behavior. Changing the format itself still needs an ADR plus updated
/// golden fixtures on both sides of the contract (CLAUDE.md).
/// </remarks>
public sealed class TranslationFileParserNaughtyStringTests
{
    private readonly TranslationFileParser _parser = new();

    /// <summary>
    /// The escape the exporter applies to every fragment before writing a line
    /// (<c>ExportTextsQueryHandler</c>): real newlines become two-character sequences so a fragment
    /// stays on one line.
    /// </summary>
    /// <remarks>
    /// KEEP IN SYNC with <c>ExportTextsQueryHandler</c> — the handler escapes inline while streaming
    /// to a file, so there is no seam to call and the rule is duplicated here. #596 changes that
    /// escape; this copy must move with it or these theories will keep passing while describing a
    /// pipeline that no longer exists.
    /// </remarks>
    private static string EscapeAsExporter(string text)
        => text.Replace("\r", "\\r").Replace("\n", "\\n");

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentWrittenByTheExporter_ShouldRoundTripExactly(string naughty)
    {
        // Arrange
        string line = $"620756992||1001||{EscapeAsExporter(naughty)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(naughty);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentCarryingTheFieldSeparator_ShouldRecoverExactContent(string naughty)
    {
        // Arrange — the separator is legal inside content; the parser anchors from both ends.
        string content = $"{naughty}||{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentOpeningWithTheCommentMarker_ShouldStillParse(string naughty)
    {
        // Arrange — '#' only starts a comment at the start of a LINE; a content field never is one.
        string content = $"#{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentSpanningRealNewlines_ShouldSurviveTheExporterEscape(string naughty)
    {
        // Arrange — the one transformation the contract mandates: a fragment holding real newlines
        // is folded onto a single line on export and unfolded on import.
        string content = $"{naughty}\r\n{naughty}\n{naughty}\r{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [InlineData(@"C:\notes", "C:" + "\u000A" + "otes")]
    [InlineData(@"backslash \n here", "backslash " + "\u000A" + " here")]
    [InlineData(@"\r", "\u000D")]
    [InlineData(@"\\n", "\\" + "\u000A")]
    public void ParseLine_ContentCarryingALiteralBackslashEscapeSequence_ShouldBeLossy(string content, string expectedLossyContent)
    {
        // Arrange — DOCUMENTED CURRENT BEHAVIOR, not an endorsement: the exporter escapes only REAL
        // newlines, so a backslash that the source text itself carries in front of 'r'/'n' reaches
        // the parser indistinguishable from an escape and is unfolded into a control character.
        // The transform is therefore not injective and this direction loses data. Tracked as #596;
        // pinned here so fixing it is a deliberate, visible change to this assertion, never an
        // accident.
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(expectedLossyContent);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentOpeningWithTheFieldSeparator_ShouldRecoverExactContent(string naughty)
    {
        // Arrange — a LEADING pipe run is safe in either parity, because the split is greedy from
        // the left and the content boundary has already been fixed by then. Its trailing twin is
        // not; that asymmetry is the point of the next test.
        string content = $"|{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [InlineData("abc|", "abc")]
    [InlineData("abc|||", "abc||")]
    [InlineData("|", "")]
    [InlineData("|||", "||")]
    public void ParseLine_ContentEndingInAnOddNumberOfPipes_ShouldLoseTheLastPipe(string content, string expectedLossyContent)
    {
        // Arrange — DOCUMENTED CURRENT BEHAVIOR, not an endorsement. Split is greedy left to right,
        // so a trailing pipe merges with the separator that follows it and the content boundary
        // lands one character early: the pipe is swallowed and reappears glued to the args column.
        // No entry of the naughty list ends in an odd pipe run, so this collision has to be composed
        // by hand. Tracked as #597; pinned here so fixing it is a deliberate, visible change to this
        // assertion, never an accident.
        string line = $"620756992||1001||{content}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(expectedLossyContent);
        result.Value.ArgsOrder.ShouldBeNull(); // "|NULL" is swallowed by ParseArgsArray's bare catch
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NonAsciiDigitsInTheFileIdColumn_ShouldFailInsteadOfMisParsing(string naughtyDigits)
    {
        // Act — a fullwidth or Arabic-Indic "number" addresses no real DAT subfile; silently
        // resolving it to some other fragment would patch the wrong text.
        Result<Translation> result = _parser.ParseLine($"{naughtyDigits}||1001||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NonAsciiDigitsInTheGossipIdColumn_ShouldFailInsteadOfMisParsing(string naughtyDigits)
    {
        // Act
        Result<Translation> result = _parser.ParseLine($"620756992||{naughtyDigits}||Content||NULL||NULL||1");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyStringInEveryColumn_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Arrange — the whole line is hostile: both id columns, the content and both args columns.
        string line = string.Join("||", naughty, naughty, naughty, naughty, naughty, naughty);

        // Act & Assert — a per-line failure is a legitimate outcome (the parser warn-skips it);
        // an escaping exception is not, because it would abort the whole patch run.
        Should.NotThrow(() => _parser.ParseLine(line));
    }
}
