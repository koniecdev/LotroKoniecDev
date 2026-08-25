using System.Globalization;

namespace LotroKoniecDev.Frontend.Infrastructure.Formatting;

/// <summary>
/// The one place a stored instant becomes a date a user reads (#736). Static SSR renders on the server,
/// and the server's own zone is UTC in a container. A request carries no time zone either, so the page
/// cannot know where the reader is. The product serves Polish users, so every visible date is converted
/// to the product's home zone. Raw UTC reads two hours early in summer, and near midnight it even names
/// the wrong day. The auth pages and e-mails follow the same rule through their own copy of this helper
/// (<c>AuthSystem.API.Extensions.DateTimeOffsetExtensions</c>).
/// </summary>
internal static class DateTimeOffsetExtensions
{
    private const string PolandTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// The zone is named on the value, not once per page. A date then stays clear wherever it is read,
    /// copied into a bug report or quoted back to support.
    /// </summary>
    private const string LabelledPolandTimeFormat = $"{PolandTimeFormat} 'czasu polskiego'";

    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);

    /// <summary>
    /// Poland time with the zone spelled out, for a timestamp that stands on its own.
    /// </summary>
    public static string ToPolandTimeText(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(LabelledPolandTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Poland time without the zone label, for a sentence that names the zone in its own words.
    /// </summary>
    public static string ToPolandTimeTextWithoutZone(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(PolandTimeFormat, CultureInfo.InvariantCulture);
}
