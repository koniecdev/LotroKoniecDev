using System.Globalization;

namespace LotroKoniecDev.Frontend.Infrastructure.Formatting;

/// <summary>
/// The one place a stored instant becomes a date a user reads (#736). Static SSR knows no reader time
/// zone and the container runs in UTC, so every visible date is Poland time. The auth API has its own copy
/// (<c>AuthSystem.API.Extensions.DateTimeOffsetExtensions</c>).
/// </summary>
internal static class DateTimeOffsetExtensions
{
    private const string PolandTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// The zone is named on the value, not once per page, so a date stays clear when copied into a bug
    /// report.
    /// </summary>
    private const string LabelledPolandTimeFormat = $"{PolandTimeFormat} 'czasu polskiego'";

    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);

    /// <summary>For a timestamp that stands on its own.</summary>
    public static string ToPolandTimeText(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(LabelledPolandTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>For a sentence that names the zone in its own words.</summary>
    public static string ToPolandTimeTextWithoutZone(this DateTimeOffset value) =>
        value.ToPolandTime().ToString(PolandTimeFormat, CultureInfo.InvariantCulture);
}
