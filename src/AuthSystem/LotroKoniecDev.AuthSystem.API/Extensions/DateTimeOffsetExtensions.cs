using System.Globalization;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The one place a stored instant becomes text that an auth page or e-mail prints (#736). The server
/// runs in UTC, but the users are in Poland, so every date is shown in Poland time. The text names the
/// zone, because near midnight a reader in another zone would see another day (#812). It also names
/// the hour and minute, because a deletion can fall just after midnight (#890). The frontend has its
/// own copy with the same digits (<c>Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions</c>),
/// so a page and an e-mail name the same minute. A new auth date format goes in this class too.
/// </summary>
internal static class DateTimeOffsetExtensions
{
    private const string LabelledPolandDateTimeFormat = "yyyy-MM-dd 'o' HH:mm 'czasu polskiego'";

    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);

    public static string ToPolandDateTimeText(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(LabelledPolandDateTimeFormat, CultureInfo.InvariantCulture);
}
