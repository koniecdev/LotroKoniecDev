using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The TMS' copy of the content escape used in the <c>||</c> file (ADR-0039, #596). The patcher has an
/// identical copy in its own assembly with its own test suite, because the two contexts share the file
/// and never the code, and <see cref="ParserContractParityTests"/> checks that the two copies agree byte
/// for byte.
/// </summary>
public sealed class TranslationLineEscaperTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    [InlineData("a\rb", @"a\rb")]
    [InlineData("a\nb", @"a\nb")]
    [InlineData("a\r\nb", @"a\r\nb")]
    [InlineData(@"C:\notes", @"C:\\notes")]
    [InlineData(@"a\nb", @"a\\nb")]
    [InlineData("\\", @"\\")]
    public void Escape_WithAnEscapableCharacter_ShouldFoldIt(string raw, string expected)
        => TranslationLineEscaper.Escape(raw).ShouldBe(expected);

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\r\nb", "a\r\nb")]
    [InlineData(@"C:\\notes", @"C:\notes")]
    [InlineData(@"a\\nb", @"a\nb")]
    public void Unescape_WithAnEscapeSequence_ShouldUnfoldIt(string escaped, string expected)
        => TranslationLineEscaper.Unescape(escaped).ShouldBe(expected);

    [Theory]
    [InlineData(@"a\tb")]
    [InlineData(@"a\qb")]
    [InlineData(@"trailing\")]
    [InlineData(@"\")]
    public void Unescape_WithASequenceNoWriterCanProduce_ShouldKeepItVerbatim(string legacy)
        => TranslationLineEscaper.Unescape(legacy).ShouldBe(legacy);

    [Theory]
    [InlineData(@"\\n", @"\n")]
    [InlineData(@"path\\to", @"path\to")]
    public void Unescape_OnAFileWrittenBeforeTheEscapeChange_ShouldDivergeFromTheOldReader(string legacy, string expected)
    {
        // ACCEPTED DIVERGENCE, pinned so a future change to it is deliberate. A pre-ADR-0039 writer
        // never escaped the backslash, so a doubled backslash meant two characters; the old reader
        // answered "\" + LF for the first case and left the second untouched. The new reader collapses
        // the pair, which is the only reading a conforming writer can have meant. Re-export before
        // re-importing a legacy file (ADR-0039, "The migration cost").
        TranslationLineEscaper.Unescape(legacy).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Line1\rLine2")]
    [InlineData(@"C:\notes")]
    [InlineData(@"\r")]
    [InlineData(@"\n")]
    [InlineData(@"\\n")]
    [InlineData("\\")]
    [InlineData("")]
    [InlineData("Zażółć gęślą jaźń")]
    [InlineData("Tekst z <--DO_NOT_TOUCH!--> argumentem")]
    [InlineData(@"a||b" + "\n" + "c")]
    public void EscapeThenUnescape_WithAHandComposedHazard_ShouldReturnTheOriginal(string raw)
        => TranslationLineEscaper.Unescape(TranslationLineEscaper.Escape(raw)).ShouldBe(raw);

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void EscapeThenUnescape_OnNaughtyContent_ShouldReturnTheOriginal(string naughty)
        => TranslationLineEscaper.Unescape(TranslationLineEscaper.Escape(naughty)).ShouldBe(naughty);

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData(@"C:\notes")]
    [InlineData("mixed \\ and \r\n together")]
    public void Escape_WithARealNewline_ShouldNeverEmitACharacterThatSplitsTheLine(string raw)
    {
        // Act
        string escaped = TranslationLineEscaper.Escape(raw);

        // Assert: the whole point of the escape: one row is one line.
        escaped.ShouldNotContain("\r");
        escaped.ShouldNotContain("\n");
    }

    [Fact]
    public void Escape_WithNull_ShouldThrowInsteadOfReturningNull()
        => Should.Throw<ArgumentNullException>(() => TranslationLineEscaper.Escape(null!));

    [Fact]
    public void Unescape_WithNull_ShouldThrowInsteadOfReturningNull()
        => Should.Throw<ArgumentNullException>(() => TranslationLineEscaper.Unescape(null!));
}
