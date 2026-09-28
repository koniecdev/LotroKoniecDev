using System.Globalization;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The one place a stored instant becomes a date that an auth page or e-mail prints (#736, #812). The
/// server's own zone is UTC in a container, and lotro-translator.pl serves Polish users, so every
/// visible date is converted to Poland time. A date that stands on its own also names the zone, because
/// a reader in another zone cannot tell which day is meant near midnight. That includes an e-mail's
/// preheader, which the inbox list shows with nothing around it. The date also carries the hour and
/// minute, because the moment it names can fall just after midnight (#890). The digits match the
/// frontend's own copy of this helper (<c>Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions</c>),
/// so a page and an e-mail name the same minute. No auth text names the zone in its own words today. The
/// first one that does gets an unlabelled twin of <see cref="ToPolandDateTimeText"/> here, so the format
/// stays in this class.
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
