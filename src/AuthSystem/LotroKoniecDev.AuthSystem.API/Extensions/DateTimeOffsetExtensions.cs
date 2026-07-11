namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class DateTimeOffsetExtensions
{
    private static readonly TimeZoneInfo PolandTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    /// <summary>
    /// User-facing deadlines are rendered in the product's home timezone (lotro-translator.pl
    /// serves a Polish audience). Formatting the raw UTC instant instead can state a
    /// calendar day one earlier than the user's local perception near midnight.
    /// </summary>
    public static DateTimeOffset ToPolandTime(this DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, PolandTimeZone);
}
