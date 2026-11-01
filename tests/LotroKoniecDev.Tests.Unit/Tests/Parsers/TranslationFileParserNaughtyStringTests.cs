using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Parsers;

/// <summary>
/// Hostile-input coverage for the patcher's half of the <c>||</c> contract (#569). It runs the Big List
/// of Naughty Strings through the pair the CLI uses: the escape <c>ExportTextsQueryHandler</c>
/// applies when it writes <c>exported.txt</c>, and <see cref="TranslationFileParser.ParseLine"/>
/// reading it back.
/// </summary>
public sealed class TranslationFileParserNaughtyStringTests
{
    private readonly TranslationFileParser _parser = new();

    /// <summary>The real escape <c>ExportTextsQueryHandler</c> calls (ADR-0039), not a copy.</summary>
    private static string EscapeAsExporter(string text)
        => TranslationLineEscaper.Escape(text);

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
        // Arrange: the separator is legal inside content; the parser anchors from both ends.
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
        // Arrange: '#' starts a comment only at the start of a line, never inside a content field.
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
        // Arrange: a fragment holding real newlines is folded onto one line on export and unfolded
        // on import.
        string content = $"{naughty}\r\n{naughty}\n{naughty}\r{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [InlineData(@"C:\notes")]
    [InlineData(@"backslash \n here")]
    [InlineData(@"\r")]
    [InlineData(@"\\n")]
    public void ParseLine_ContentCarryingALiteralBackslashEscapeSequence_ShouldRoundTripExactly(string content)
    {
        // Arrange: the escape escapes its own escape character (ADR-0039), so a backslash the
        // source text carries in front of 'r'/'n' never unfolds into a control character (#596).
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NaughtyContentOpeningWithTheFieldSeparator_ShouldRecoverExactContent(string naughty)
    {
        // Arrange: a leading pipe run is safe in either parity: the forward scan takes the first two
        // pipes of the run as the separator, and the rest stays content. The trailing run is the
        // hard case, tested next.
        string content = $"|{naughty}";
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
    }

    [Theory]
    [InlineData("abc|")]
    [InlineData("abc||")]
    [InlineData("abc|||")]
    [InlineData("|")]
    [InlineData("||")]
    [InlineData("|||")]
    public void ParseLine_ContentEndingInAnOddNumberOfPipes_ShouldRoundTripExactly(string content)
    {
        // Arrange: the trailing boundary is found by scanning backward (ADR-0042), so the last two
        // pipes of a run are the separator and every earlier one stays content (#597). No entry of
        // the naughty list ends in an odd pipe run, so these cases are written by hand.
        string line = $"620756992||1001||{EscapeAsExporter(content)}||NULL||NULL||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
        result.Value.ArgsOrder.ShouldBeNull();
        result.Value.ArgsId.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.PipeRuns), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_EveryPipeRunUpToSixCharacters_ShouldRoundTripAndLeaveTheArgsColumnsClean(string content)
    {
        // Arrange: every string over the alphabet this format is weakest at, in both parities, with
        // runs at the leading boundary, the trailing one, or both.
        string line = $"620756992||1001||{EscapeAsExporter(content)}||1-2||3-4||1";

        // Act
        Result<Translation> result = _parser.ParseLine(line);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.ShouldBe(content);
        result.Value.ArgsOrder.ShouldBe([0, 1]);
        result.Value.ArgsId.ShouldBe([2, 3]);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public void ParseLine_NonAsciiDigitsInTheFileIdColumn_ShouldFailInsteadOfMisParsing(string naughtyDigits)
    {
        // Act: a fullwidth or Arabic-Indic "number" addresses no real subfile; resolving it to some
        // other fragment would patch the wrong text.
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
        // Arrange: the whole line is hostile: both id columns, the content and both args columns.
        string line = string.Join("||", naughty, naughty, naughty, naughty, naughty, naughty);

        // Act & Assert: a per-line failure is fine; an exception would abort the whole patch run.
        Should.NotThrow(() => _parser.ParseLine(line));
    }
}
