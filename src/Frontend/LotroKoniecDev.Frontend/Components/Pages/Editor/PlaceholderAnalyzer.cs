namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Pure analysis of the <c>&lt;--DO_NOT_TOUCH!--&gt;</c> argument placeholder for the side-by-side
/// editor: it counts the token in a piece of text and splits the text into literal / placeholder runs
/// so the page can highlight the markers without an interactive layer. The token is the inter-context
/// file contract (a piece separator); the Frontend owns its own copy as a constant rather than
/// referencing the frozen patcher, exactly as each bounded context owns its own parser. Kept isolated
/// so the count / comparison / segmentation rules are unit-testable without rendering the component
/// (the Frontend has no bUnit).
/// </summary>
internal static class PlaceholderAnalyzer
{
    /// <summary>The argument placeholder marker; translators must keep it verbatim (file-format contract).</summary>
    internal const string Placeholder = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// Counts how many <see cref="Placeholder"/> markers occur in <paramref name="text"/>. A
    /// <c>null</c> or empty text has none.
    /// </summary>
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        int index = text.IndexOf(Placeholder, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(Placeholder, index + Placeholder.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Compares the placeholder count of the English source against the Polish translation. The result
    /// reports whether the counts match and carries both counts so the page can render an exact warning
    /// (the warning is advisory — the API stores the Polish verbatim either way, spec 0001 / #100).
    /// Untranslated Polish (no text yet) is never flagged: there is nothing to warn about until the
    /// translator types.
    /// </summary>
    public static PlaceholderComparison Compare(string sourceText, string? translatedText)
    {
        int sourceCount = Count(sourceText);

        if (string.IsNullOrEmpty(translatedText))
        {
            return new PlaceholderComparison(sourceCount, 0, IsMatch: true);
        }

        int translatedCount = Count(translatedText);
        return new PlaceholderComparison(sourceCount, translatedCount, sourceCount == translatedCount);
    }

    /// <summary>
    /// Splits <paramref name="text"/> into ordered runs, each flagged as the placeholder marker or
    /// literal text, so the page can wrap only the markers in a highlight span. Adjacent literals are
    /// never produced; an empty text yields no segments.
    /// </summary>
    public static IReadOnlyList<PlaceholderSegment> Segment(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<PlaceholderSegment> segments = [];
        int cursor = 0;

        int index = text.IndexOf(Placeholder, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index > cursor)
            {
                segments.Add(new PlaceholderSegment(text[cursor..index], IsPlaceholder: false));
            }

            segments.Add(new PlaceholderSegment(Placeholder, IsPlaceholder: true));
            cursor = index + Placeholder.Length;
            index = text.IndexOf(Placeholder, cursor, StringComparison.Ordinal);
        }

        if (cursor < text.Length)
        {
            segments.Add(new PlaceholderSegment(text[cursor..], IsPlaceholder: false));
        }

        return segments;
    }
}

/// <summary>
/// The outcome of comparing English vs Polish placeholder counts: the two counts and whether they
/// match. <see cref="IsMatch"/> is <c>true</c> for still-untranslated Polish (nothing to warn about).
/// </summary>
internal sealed record PlaceholderComparison(int SourceCount, int TranslatedCount, bool IsMatch);

/// <summary>One ordered run of text from <see cref="PlaceholderAnalyzer.Segment"/>: a literal or a marker.</summary>
internal sealed record PlaceholderSegment(string Text, bool IsPlaceholder);
