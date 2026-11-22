using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The English source the import diff compares. Equality covers the text and both args columns,
/// because a new placeholder layout changes the meaning even when the text stays the same (spec 0001).
/// The text is raw, never escaped (ADR-0039); the args columns keep their file form (<c>1-2-3</c>).
/// </summary>
public sealed class TranslationSource : ValueObject
{
    public string Text { get; }
    public string? ArgsOrder { get; }
    public string? ArgsId { get; }

    /// <summary>
    /// Removes the line breaks a translator adds by pressing Enter at the end of the Polish, unless this
    /// English ends with one (#1476, ADR-0039 §6). Spaces, tabs, the typed two characters <c>\n</c> and
    /// breaks inside the text stay. A text of only line breaks comes back empty, so the caller refuses a
    /// blank text first.
    /// </summary>
    public string RemoveTrailingLineBreaksFrom(string translatedText)
    {
        ArgumentNullException.ThrowIfNull(translatedText);

        return Text.EndsWith('\r') || Text.EndsWith('\n')
            ? translatedText
            : translatedText.TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Refuses Polish with a different number of <see cref="DatFormatConstants.Placeholder"/> markers
    /// than this English (#1437). The patcher cuts the text on the marker and the game puts an argument
    /// into each cut, so no other count can work in the game.
    /// </summary>
    public Result CheckPlaceholderCountOf(string translatedText)
    {
        ArgumentNullException.ThrowIfNull(translatedText);

        int englishCount = CountPlaceholders(Text);
        int polishCount = CountPlaceholders(translatedText);

        return englishCount == polishCount
            ? Result.Success()
            : Result.Failure(
                DomainErrors.TranslationEntity.TranslatedTextProperty.PlaceholderCountMismatch(englishCount, polishCount));
    }

    /// <summary>Counts the markers without overlap, the same way the patcher's ordinal cut does.</summary>
    public static int CountPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int count = 0;
        int index = text.IndexOf(DatFormatConstants.Placeholder, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(
                DatFormatConstants.Placeholder, index + DatFormatConstants.Placeholder.Length, StringComparison.Ordinal);
        }

        return count;
    }

    public static Result<TranslationSource> Create(string text, string? argsOrder, string? argsId)
    {
        // An empty fragment is valid game content, so only null is refused, and the parser never
        // produces one: a programmer error, not a row to reject.
        ArgumentNullException.ThrowIfNull(text);

        TranslationSource instance = new(text, NormalizeArgs(argsOrder), NormalizeArgs(argsId));

        return Result.Success(instance);
    }

    private TranslationSource(string text, string? argsOrder, string? argsId)
    {
        Text = text;
        ArgsOrder = argsOrder;
        ArgsId = argsId;
    }

    // Blank and "NULL" both mean "no arguments", so the diff must not read them as a source change.
    private static string? NormalizeArgs(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Text;
        yield return ArgsOrder ?? string.Empty;
        yield return ArgsId ?? string.Empty;
    }
}
