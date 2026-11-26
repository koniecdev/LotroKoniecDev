namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

/// <summary>
/// The save's rule for line breaks at the end of the Polish (#1476, ADR-0039 §6). The domain, the editor's
/// warnings and its "unchanged" check (#1490) share this one copy, so they can never disagree.
/// </summary>
public static class TrailingLineBreaks
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// Removes the line breaks a translator adds by pressing Enter at the end of the Polish, unless the
    /// English ends with one. Spaces, tabs, the typed two characters <c>\n</c> and breaks inside the text
    /// stay. A text of only line breaks comes back empty.
    /// </summary>
    public static string RemoveFrom(string translatedText, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(sourceText);

        return sourceText.EndsWith('\r') || sourceText.EndsWith('\n')
            ? translatedText
            : translatedText.TrimEnd(LineBreaks);
    }
}
