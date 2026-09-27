using System.Globalization;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The one place a stored instant becomes a date that an auth page or e-mail prints (#736, #812). The
/// server's own zone is UTC in a container, and lotro-translator.pl serves Polish users, so every
/// visible date is converted to Poland time. A date that stands on its own also names the zone, because
/// a reader in another zone cannot tell which day is meant near midnight. That includes an e-mail's
/// preheader, which the inbox list shows with nothing around it. A sentence that already names the zone
/// in its own words formats <see cref="ToPolandTime"/> itself instead. The frontend follows the same
/// rule through its own copy (<c>Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions</c>).
/// </summary>
internal static class DateTimeOffsetExtensions
{
    private const string LabelledPolandDateFormat = "yyyy-MM-dd 'czasu polskiego'";

    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);

    public static string ToPolandDateText(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(LabelledPolandDateFormat, CultureInfo.InvariantCulture);
}
