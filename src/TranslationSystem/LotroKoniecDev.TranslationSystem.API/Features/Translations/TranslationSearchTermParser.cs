using System.Globalization;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Reads the ids in a list search term (#1406). Only the ASCII digits 0–9 count, with no sign, space or
/// separator; anything else names no id, and the text search still runs. The explicit digit check is
/// needed because <see cref="int.TryParse(string, NumberStyles, IFormatProvider, out int)"/> accepts
/// trailing <c>\0</c> characters.
/// </summary>
internal static class TranslationSearchTermParser
{
    private const char PairSeparator = ':';

    public static TranslationSearchIds Parse(string trimmedTerm)
    {
        if (IsAsciiDigits(trimmedTerm))
        {
            return new TranslationSearchIds(
                FileId: ParseInt(trimmedTerm),
                GossipId: ParseLong(trimmedTerm),
                PairFileId: null,
                PairGossipId: null);
        }

        int separatorIndex = trimmedTerm.IndexOf(PairSeparator);
        if (separatorIndex < 0)
        {
            return TranslationSearchIds.None;
        }

        string fileIdPart = trimmedTerm[..separatorIndex];
        string gossipIdPart = trimmedTerm[(separatorIndex + 1)..];

        // A second separator is not a digit, so "1:2:3" fails here.
        if (!IsAsciiDigits(fileIdPart) || !IsAsciiDigits(gossipIdPart))
        {
            return TranslationSearchIds.None;
        }

        return ParseInt(fileIdPart) is { } pairFileId && ParseLong(gossipIdPart) is { } pairGossipId
            ? new TranslationSearchIds(FileId: null, GossipId: null, pairFileId, pairGossipId)
            : TranslationSearchIds.None;
    }

    private static int? ParseInt(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static long? ParseLong(string digits) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static bool IsAsciiDigits(string text)
    {
        if (text.Length is 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
