using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

/// <summary>
/// Every date an auth page or e-mail prints goes through here (#736, #812). The cases pin both sides of
/// the daylight-saving switch and an evening instant that is already the next day in Poland.
/// </summary>
public sealed class DateTimeOffsetExtensionsTests
{
    [Theory]
    // Summer: Europe/Warsaw is CEST, UTC+2.
    [InlineData("2026-08-24T21:48:00Z", "2026-08-24 czasu polskiego")]
    // Winter: Europe/Warsaw is CET, UTC+1.
    [InlineData("2026-12-10T10:00:00Z", "2026-12-10 czasu polskiego")]
    // Still the 24th in UTC, already the 25th in Poland. This is why raw UTC names the wrong day.
    [InlineData("2026-08-24T22:30:00Z", "2026-08-25 czasu polskiego")]
    [InlineData("2026-12-31T23:30:00Z", "2027-01-01 czasu polskiego")]
    // An input with its own non-UTC offset still converts by instant.
    [InlineData("2026-08-24T18:30:00-05:00", "2026-08-25 czasu polskiego")]
    public void ToPolandDateText_RendersThePolishDateWithTheZoneNamed(string instant, string expected)
    {
        DateTimeOffset value = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        value.ToPolandDateText().ShouldBe(expected);
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
