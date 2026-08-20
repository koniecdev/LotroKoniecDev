namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Works out where the <c>&lt;--DO_NOT_TOUCH!--&gt;</c> argument placeholder sits, for the side-by-side
/// editor. It counts the marker in a piece of text and splits the text into plain runs and marker runs,
/// so the page can highlight the markers without any interactive code.
/// The marker is part of the file contract between the two contexts. The Frontend keeps its own copy as
/// a constant instead of referencing the patcher, exactly as each context has its own parser.
/// It is kept separate so the counting, comparing and splitting rules can be unit-tested directly,
/// without rendering the component. The highlighting itself is covered by bUnit tests.
/// </summary>
internal static class PlaceholderAnalyzer
{
    /// <summary>The argument placeholder. Translators must keep it exactly as it is (file contract).</summary>
    internal const string Placeholder = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// Counts the <see cref="Placeholder"/> markers in <paramref name="text"/>. A <c>null</c> or empty
    /// text has none.
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
    /// Compares how many placeholders the English source has with how many the Polish has. The result
    /// says whether they match and carries both numbers, so the page can show an exact warning. The
    /// warning is only advice: the API stores the Polish as it is either way (spec 0001, #100).
    /// A row with no Polish yet is never flagged, because there is nothing to warn about until the
    /// translator starts typing.
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
    /// Splits <paramref name="text"/> into runs in order, each marked as either the placeholder or plain
    /// text, so the page can highlight the markers only. Two plain runs never follow each other, and an
    /// empty text produces no runs.
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
/// The result of comparing the placeholder counts: both numbers and whether they match.
/// <see cref="IsMatch"/> is <c>true</c> when there is no Polish yet, because there is nothing to warn
/// about.
/// </summary>
internal sealed record PlaceholderComparison(int SourceCount, int TranslatedCount, bool IsMatch);

/// <summary>One run of text from <see cref="PlaceholderAnalyzer.Segment"/>: plain text or a marker.</summary>
internal sealed record PlaceholderSegment(string Text, bool IsPlaceholder);
