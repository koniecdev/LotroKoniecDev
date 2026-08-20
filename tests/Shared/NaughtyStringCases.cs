using NaughtyStrings;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Shared xUnit theory sources over the Big List of Naughty Strings (the <c>NaughtyStrings</c>
/// package, #569). The file is <em>linked</em> into every pure unit suite that hardens a
/// place that handles a lot of strings, so the list of hostile inputs is written once instead of copied
/// into each project. A suite picks the smallest source that still covers what it tests.
/// </summary>
/// <remarks>
/// The categories below are grouped by the danger they pose to this codebase, a UTF-16 binary format on
/// one side of the <c>||</c> contract and a line-based text file on the other, and not by the categories
/// the upstream list uses.
/// </remarks>
internal static class NaughtyStringCases
{
    /// <summary>
    /// Every naughty string the package ships (550 distinct). The default source for round-trip
    /// theories: nothing here may crash a parser, a serializer or a value object factory.
    /// </summary>
    /// <remarks>
    /// The list carries no empty entry (its shortest is one character) and no entry ending in an
    /// odd run of <c>|</c>, so the empty-content and pipe-boundary cases of the <c>||</c> contract
    /// have to be written by hand. See the explicit theories in the parser suites.
    /// </remarks>
    public static TheoryData<string> All => ToTheoryData(AllValues);

    /// <summary>
    /// The same list as <see cref="All"/>, unwrapped, for a suite that has to narrow the set down to the
    /// inputs its code actually accepts before it builds its own <see cref="TheoryData{T}"/>.
    /// Filtering here keeps the assertion unconditional, which a branch inside the test would not.
    /// </summary>
    public static IReadOnlyList<string> AllValues { get; } =
        TheNaughtyStrings.All.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Strings whose UTF-16 representation is the hazard: surrogate pairs (emoji), two-byte and
    /// characters outside the basic plane, combining marks, bidi controls and invisible code points. These
    /// are what a <c>char</c>-counting binary writer gets wrong.
    /// </summary>
    public static TheoryData<string> UnicodeHazards => ToTheoryData(
    [
        .. TheNaughtyStrings.Emoji,
        .. TheNaughtyStrings.TwoByteCharacters,
        .. TheNaughtyStrings.Stringswhichcontaintwobyteletters,
        .. TheNaughtyStrings.SpecialUnicodeCharactersUnion,
        .. TheNaughtyStrings.RightToLeftStrings,
        .. TheNaughtyStrings.TrickUnicode,
        .. TheNaughtyStrings.ZalgoText,
        .. TheNaughtyStrings.UnicodeSubscriptSuperscriptAccents,
        .. TheNaughtyStrings.UnicodeUpsidedown,
        .. TheNaughtyStrings.Unicodefont,
        .. TheNaughtyStrings.OghamText,
        .. TheNaughtyStrings.Changinglengthwhenlowercased
    ]);

    /// <summary>
    /// A mix of quotes, escapes and punctuation: the strings most likely to clash with a delimited text
    /// format. It includes ASCII punctuation, among it <c>|</c>, <c>#</c> and the backslash, curly and
    /// misplaced quotation marks, injection payloads and terminal escape codes.
    /// </summary>
    public static TheoryData<string> DelimiterHazards => ToTheoryData(
    [
        .. TheNaughtyStrings.SpecialCharacters,
        .. TheNaughtyStrings.QuotationMarks,
        .. TheNaughtyStrings.UnicodeSymbols,
        .. TheNaughtyStrings.SpecialWordCharacters,
        .. TheNaughtyStrings.ScriptInjection,
        .. TheNaughtyStrings.SQLInjection,
        .. TheNaughtyStrings.ServerCodeInjection,
        .. TheNaughtyStrings.CommandInjectionRuby,
        .. TheNaughtyStrings.UnwantedInterpolation,
        .. TheNaughtyStrings.FileInclusion,
        .. TheNaughtyStrings.Terminalescapecodes
    ]);

    /// <summary>
    /// Every string of up to six characters made of <c>a</c> and <c>|</c>: 127 entries covering every run
    /// of pipes the <c>||</c> contract can meet at either end of the content, odd and even, including the
    /// empty string.
    /// The naughty list has none of these, because no entry ends in an odd run of <c>|</c>, and
    /// hand-picked cases only sample the problem: an odd run at the end quietly lost its last pipe into
    /// the args column for the whole life of the format (#597, fixed by ADR-0042).
    /// Testing all of them is cheap here and protects the carving from a future "simplification" back to
    /// <c>string.Split</c>.
    /// </summary>
    public static TheoryData<string> PipeRuns => ToTheoryData(BuildPipeRuns(maxLength: 6));

    private static IEnumerable<string> BuildPipeRuns(int maxLength)
    {
        List<string> combinations = [string.Empty];
        List<string> previousLength = [string.Empty];

        for (int length = 1; length <= maxLength; length++)
        {
            List<string> currentLength = new(previousLength.Count * 2);

            foreach (string prefix in previousLength)
            {
                currentLength.Add(prefix + 'a');
                currentLength.Add(prefix + '|');
            }

            combinations.AddRange(currentLength);
            previousLength = currentLength;
        }

        return combinations;
    }

    /// <summary>
    /// Digits a person reads as a number but <see cref="int.TryParse(string, out int)"/> does not:
    /// fullwidth, Arabic-Indic and other non-ASCII numerals. They target the two id columns of the
    /// <c>||</c> contract, which must reject them and not read them as the wrong fragment.
    /// </summary>
    public static TheoryData<string> NonAsciiDigits => ToTheoryData(TheNaughtyStrings.UnicodeNumbers);

    /// <summary>
    /// Every naughty string the API's <c>NotEmpty()</c> check lets through as translated text, meaning it
    /// is not blank once whitespace is ignored. That is exactly the set a translator can really get into
    /// the <c>TranslatedText</c> column, and therefore into the distributed file.
    /// </summary>
    public static TheoryData<string> SubmittableText
        => ToTheoryData(AllValues.Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>
    /// The mirror image of <see cref="SubmittableText"/>: the entries the API's <c>NotEmpty()</c>
    /// guard rejects, and therefore the ones that must never reach a domain guard expecting
    /// non-blank text.
    /// </summary>
    public static TheoryData<string> BlankText
        => ToTheoryData(AllValues.Where(string.IsNullOrWhiteSpace));

    /// <summary>
    /// Deduplicated on purpose: the upstream list repeats two entries, <c>-</c> and a curly-quote tag, and
    /// the merged categories above overlap. xUnit builds a test-case id from the argument, so a duplicate
    /// is dropped anyway, but only after it logs a "Skipping test case with duplicate ID" line on every
    /// run.
    /// </summary>
    private static TheoryData<string> ToTheoryData(IEnumerable<string> values)
    {
        TheoryData<string> data = [];

        foreach (string value in values.Distinct(StringComparer.Ordinal))
        {
            data.Add(value);
        }

        return data;
    }
}
