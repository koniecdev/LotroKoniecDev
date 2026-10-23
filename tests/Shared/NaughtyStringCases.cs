using NaughtyStrings;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Theory sources over the Big List of Naughty Strings (#569), linked into every pure unit suite that
/// needs them, so the hostile inputs are written once. A suite picks the smallest source that still
/// covers what it tests.
/// </summary>
/// <remarks>
/// The sources are grouped by the danger they pose here (a UTF-16 binary format on one side of the
/// <c>||</c> contract, a line-based text file on the other), not by the upstream categories.
/// </remarks>
internal static class NaughtyStringCases
{
    /// <summary>
    /// The default source for round-trip theories: nothing here may crash a parser, a serializer or a
    /// value object factory.
    /// </summary>
    /// <remarks>
    /// The list has no empty entry and no entry ending in an odd run of <c>|</c>, so those cases of the
    /// <c>||</c> contract are written by hand in the parser suites.
    /// </remarks>
    public static TheoryData<string> All => ToTheoryData(AllValues);

    /// <summary>
    /// For a suite that narrows the list to the inputs its code accepts before it builds its own
    /// <see cref="TheoryData{T}"/>, so the assertion stays unconditional instead of hiding in a branch.
    /// </summary>
    public static IReadOnlyList<string> AllValues { get; } =
        TheNaughtyStrings.All.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Strings whose UTF-16 form is the hazard: what a <c>char</c>-counting binary writer gets wrong.
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
    /// The strings most likely to clash with a delimited text format, among them <c>|</c>, <c>#</c> and
    /// the backslash.
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
    /// Every string of up to six characters made of <c>a</c> and <c>|</c> (127 entries, the empty string
    /// included), so every run of pipes at either end of the content is covered, odd and even. The
    /// naughty list has none of these, and hand-picked cases only sample the problem (#597, ADR-0042).
    /// Testing all of them is cheap and guards the line carving against a return to <c>string.Split</c>.
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
    /// The entries the API's <c>NotEmpty()</c> check lets through as translated text: what a translator
    /// can get into the <c>TranslatedText</c> column, and so into the distributed file.
    /// </summary>
    public static TheoryData<string> SubmittableText
        => ToTheoryData(AllValues.Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>
    /// The entries the API's <c>NotEmpty()</c> check rejects, so they never reach a domain guard that
    /// expects non-blank text.
    /// </summary>
    public static TheoryData<string> BlankText
        => ToTheoryData(AllValues.Where(string.IsNullOrWhiteSpace));

    /// <summary>
    /// Deduplicated because the upstream list repeats two entries and the merged categories overlap.
    /// xUnit drops a duplicate anyway, but logs a "Skipping test case with duplicate ID" line on every run.
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
