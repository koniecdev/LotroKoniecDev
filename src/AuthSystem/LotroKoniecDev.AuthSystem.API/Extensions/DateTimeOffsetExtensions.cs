using System.Globalization;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The one place an auth page or e-mail turns an instant into text, in Poland time (#736). It names the
/// zone (#812) and the minute, because a deletion can fall just after midnight (#890). Keep the digits in
/// step with <c>Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions</c>, so a page and an e-mail
/// name the same minute.
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
