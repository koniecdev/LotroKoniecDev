using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

/// <summary>
/// Every date an auth page or e-mail prints goes through here (#736, #812, #890). The cases pin the
/// summer and winter offsets, both clock changes, evening instants that are already the next day in
/// Poland, and the seconds that the minute drops.
/// </summary>
public sealed class DateTimeOffsetExtensionsTests
{
    [Theory]
    // Summer: Europe/Warsaw is CEST, UTC+2.
    [InlineData("2026-08-24T21:48:00Z", "2026-08-24 o 23:48 czasu polskiego")]
    // Winter: Europe/Warsaw is CET, UTC+1.
    [InlineData("2026-12-10T10:00:00Z", "2026-12-10 o 11:00 czasu polskiego")]
    // Still the 24th in UTC, already the 25th in Poland. A reader given only the day would think the
    // whole 25th is left, but the moment is half an hour into it.
    [InlineData("2026-08-24T22:30:00Z", "2026-08-25 o 00:30 czasu polskiego")]
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01 o 00:30 czasu polskiego")]
    // The clocks go back at 01:00 UTC on 2026-10-25: 02:59 CEST is followed by 02:00 CET.
    [InlineData("2026-10-25T00:59:00Z", "2026-10-25 o 02:59 czasu polskiego")]
    [InlineData("2026-10-25T01:00:00Z", "2026-10-25 o 02:00 czasu polskiego")]
    // The clocks go forward at 01:00 UTC on 2027-03-28: 01:59 CET is followed by 03:00 CEST.
    [InlineData("2027-03-28T00:59:00Z", "2027-03-28 o 01:59 czasu polskiego")]
    [InlineData("2027-03-28T01:00:00Z", "2027-03-28 o 03:00 czasu polskiego")]
    // The seconds are cut, never rounded, like on the frontend page. Rounding up could name a minute
    // after the account is already gone.
    [InlineData("2026-08-18T22:30:59.999Z", "2026-08-19 o 00:30 czasu polskiego")]
    // An input with its own non-UTC offset still converts by instant.
    [InlineData("2026-08-24T18:30:00-05:00", "2026-08-25 o 01:30 czasu polskiego")]
    public void ToPolandDateTimeText_RendersThePolishDateAndMinuteWithTheZoneNamed(string instant, string expected)
    {
        DateTimeOffset value = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        value.ToPolandDateTimeText().ShouldBe(expected);
    }

    [Fact]
    public void ToPolandTime_KeepsTheInstantAndCarriesThePolishOffset()
    {
        DateTimeOffset value = new(2026, 8, 24, 21, 48, 0, TimeSpan.Zero);

        DateTimeOffset polandTime = value.ToPolandTime();

        polandTime.Offset.ShouldBe(TimeSpan.FromHours(2));
        polandTime.UtcDateTime.ShouldBe(value.UtcDateTime);
    }
}
