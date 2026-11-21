using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// Removes the line breaks a translator adds by pressing Enter at the end of the Polish (#1476, ADR-0039
/// §6). The game's own texts never end with a real line break, so one is kept only when the English ends
/// with one. Spaces, tabs, the typed two characters <c>\n</c> and breaks inside the text stay. Every
/// path that stores Polish must call this, so that one rule holds for all of them. A text of only line
/// breaks comes back empty, so the caller refuses a blank text first.
/// </summary>
public static class TrailingLineBreaks
{
    public static string RemoveUnlessSourceEndsWithOne(string translatedText, TranslationSource source)
    {
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(source);

        return source.Text.EndsWith('\r') || source.Text.EndsWith('\n')
            ? translatedText
            : translatedText.TrimEnd('\r', '\n');
    }
}
