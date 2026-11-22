using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Counts and splits out the argument placeholder, so the editor can highlight it without interactive
/// code. The marker is part of the <c>||</c> file contract; the Frontend takes the translation API's
/// copy, so the editor's count and the API's refusal use one string.
/// </summary>
internal static class PlaceholderAnalyzer
{
    /// <summary>Translators must keep it exactly as it is (file contract).</summary>
    internal const string Placeholder = DatFormatConstants.Placeholder;

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
    /// Feeds the yellow warning the page draws. The page has no script, so a translator sees it after a
    /// save the API refused (#1437), or on a stored row whose English changed its count. A row with no
    /// Polish yet is never flagged.
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
