using NaughtyStrings;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Shared xUnit theory sources over the Big List of Naughty Strings (the <c>NaughtyStrings</c>
/// package, #569). The file is <em>linked</em> into every pure unit suite that hardens a
/// string-heavy seam, so the hostile-input matrix is declared once instead of copy-pasted per
/// project; a suite picks the narrowest source that still covers its seam.
/// </summary>
/// <remarks>
/// The categories below are grouped by the hazard they pose to <em>this</em> codebase — a
/// UTF-16 binary format on one side of the <c>||</c> contract and a line-oriented text file on the
/// other — not by the taxonomy of the upstream list.
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
    /// have to be composed by hand — see the explicit theories in the parser suites.
    /// </remarks>
    public static TheoryData<string> All => ToTheoryData(AllValues);

    /// <summary>
    /// The same list as <see cref="All"/>, raw — for a suite that has to filter the corpus down to
    /// the inputs its seam actually accepts before building its own <see cref="TheoryData{T}"/>.
    /// Filtering at the source keeps the resulting assertion unconditional, which a branch inside
    /// the test would not.
    /// </summary>
    public static IReadOnlyList<string> AllValues { get; } =
        TheNaughtyStrings.All.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Strings whose UTF-16 representation is the hazard: surrogate pairs (emoji), two-byte and
    /// astral-plane characters, combining marks, bidi controls and invisible code points. These
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
    /// Quote, escape and punctuation soup — the strings most likely to collide with a delimited
    /// text format: ASCII punctuation (including <c>|</c>, <c>#</c> and the backslash), smart and
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
    /// Digits a human reads as a number but <see cref="int.TryParse(string, out int)"/> does not —
    /// fullwidth, Arabic-Indic and other non-ASCII numerals. Aimed at the two id columns of the
    /// <c>||</c> contract, which must reject them rather than mis-parse them into a wrong fragment.
    /// </summary>
    public static TheoryData<string> NonAsciiDigits => ToTheoryData(TheNaughtyStrings.UnicodeNumbers);

    /// <summary>
    /// Every naughty string that the API's <c>NotEmpty()</c> guard lets through as translated text
    /// (i.e. not blank once whitespace is discounted) — the exact set a translator can actually get
    /// into the <c>TranslatedText</c> column, and therefore into the distributed file.
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
    /// Distinct on purpose: the upstream list repeats two entries (<c>-</c> and a smart-quote tag),
    /// and the merged category sources above overlap. xUnit derives a test-case id from the
    /// argument, so a duplicate is dropped anyway — but only after logging a
    /// "Skipping test case with duplicate ID" line on every single run.
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
