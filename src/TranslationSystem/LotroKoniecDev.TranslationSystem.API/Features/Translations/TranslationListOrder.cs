using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// One default order of the translation list, with everything that must agree with it: the sort, the
/// "after this place" filter and the place each row gets (ADR-0077). An explicit sort has no order here,
/// so its rows get no place. When "Tylko moje" moves to the newest proposal (#1384), this class changes
/// and nothing else does.
/// </summary>
internal sealed class TranslationListOrder
{
    private const char ByKeyTag = 'k';
    private const char NewestChangeFirstTag = 'm';
    private const char Separator = ':';

    /// <summary>Far longer than any place this class writes, so a huge value is refused before decoding.</summary>
    private const int MaxPlaceLength = 128;

    /// <summary>The default order: <c>(FileId, GossipId)</c>.</summary>
    public static readonly TranslationListOrder ByKey = new(ByKeyTag);

    /// <summary>"Tylko moje": the newest change first, then <c>FileId</c>, then <c>GossipId</c> (#1491).</summary>
    public static readonly TranslationListOrder NewestChangeFirst = new(NewestChangeFirstTag);

    private readonly char _tag;

    public IOrderedQueryable<TranslationReadModel> Sort(IQueryable<TranslationReadModel> rows) =>
        _tag == NewestChangeFirstTag
            ? rows
                .OrderByDescending(translation => translation.UpdatedAt)
                .ThenBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId)
            : rows
                .OrderBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId);

    /// <summary>
    /// The row's sort key with this order's tag, as base64url text. <paramref name="updatedAt"/> is written
    /// as UTC ticks, which keep PostgreSQL's microseconds exactly.
    /// </summary>
    public string PlaceOf(int fileId, long gossipId, DateTimeOffset updatedAt)
    {
        string place = _tag == NewestChangeFirstTag
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{_tag}{Separator}{updatedAt.UtcTicks}{Separator}{fileId}{Separator}{gossipId}")
            : string.Create(CultureInfo.InvariantCulture, $"{_tag}{Separator}{fileId}{Separator}{gossipId}");

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(place));
    }

    /// <summary>
    /// The filter for the rows after <paramref name="place"/> in this order. A save never moves a row to a
    /// later place, so the first row after a saved row's old place is its next row (spec 0018). Returns
    /// false, and never throws, for a place this order did not write: bad text, a number out of range, or
    /// a place of the other order.
    /// </summary>
    public bool TryReadPlace(
        string place,
        [NotNullWhen(true)] out Expression<Func<TranslationReadModel, bool>>? isAfter)
    {
        ArgumentNullException.ThrowIfNull(place);

        isAfter = null;
        if (!TryDecode(place, out string[] parts) || parts[0] != _tag.ToString())
        {
            return false;
        }

        if (_tag == NewestChangeFirstTag)
        {
            if (parts.Length != 4
                || !TryParseTicks(parts[1], out DateTimeOffset updatedAt)
                || !TryParseKey(parts[2], parts[3], out int fileId, out long gossipId))
            {
                return false;
            }

            isAfter = translation => translation.UpdatedAt < updatedAt
                                     || translation.UpdatedAt == updatedAt
                                     && (translation.FileId > fileId
                                         || translation.FileId == fileId && translation.GossipId > gossipId);
            return true;
        }

        if (parts.Length != 3 || !TryParseKey(parts[1], parts[2], out int keyFileId, out long keyGossipId))
        {
            return false;
        }

        isAfter = translation => translation.FileId > keyFileId
                                 || translation.FileId == keyFileId && translation.GossipId > keyGossipId;
        return true;
    }

    public static TranslationListOrder For(bool mine) => mine ? NewestChangeFirst : ByKey;

    private TranslationListOrder(char tag)
    {
        _tag = tag;
    }

    private static bool TryDecode(string place, out string[] parts)
    {
        parts = [];

        // TryDecodeFromChars throws on a character that is not base64url, so the text is checked first.
        if (place.Length is 0 or > MaxPlaceLength || !Base64Url.IsValid(place))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Base64Url.GetMaxDecodedLength(place.Length)];
        if (!Base64Url.TryDecodeFromChars(place, bytes, out int written))
        {
            return false;
        }

        parts = Encoding.UTF8.GetString(bytes[..written]).Split(Separator);
        return true;
    }

    private static bool TryParseTicks(string text, out DateTimeOffset updatedAt)
    {
        updatedAt = default;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        updatedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static bool TryParseKey(string fileIdText, string gossipIdText, out int fileId, out long gossipId)
    {
        gossipId = 0;
        return int.TryParse(fileIdText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out fileId)
               && long.TryParse(gossipIdText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out gossipId);
    }
}
