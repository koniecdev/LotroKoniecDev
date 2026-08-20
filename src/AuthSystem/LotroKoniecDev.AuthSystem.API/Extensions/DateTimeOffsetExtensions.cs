namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class DateTimeOffsetExtensions
{
    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    /// <summary>
    /// Dates shown to users are in the product's home time zone, because lotro-translator.pl serves a
    /// Polish audience. Printing the raw UTC value can name the day before near midnight.
    /// </summary>
    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);
}
