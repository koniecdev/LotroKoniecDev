namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Counts and splits out the argument placeholder, so the editor can highlight it without interactive
/// code. The marker is part of the <c>||</c> file contract; the Frontend keeps its own copy instead of
/// referencing the patcher, as each context has its own parser.
/// </summary>
internal static class PlaceholderAnalyzer
{
    /// <summary>Translators must keep it exactly as it is (file contract).</summary>
    internal const string Placeholder = "<--DO_NOT_TOUCH!-->";

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
    /// The warning is only advice: the API stores the Polish as it is either way (spec 0001). A row with
    /// no Polish yet is never flagged.
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

    /// <summary>Two plain runs never follow each other.</summary>
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

/// <summary><see cref="IsMatch"/> is <c>true</c> when there is no Polish yet.</summary>
internal sealed record PlaceholderComparison(int SourceCount, int TranslatedCount, bool IsMatch);

internal sealed record PlaceholderSegment(string Text, bool IsPlaceholder);
