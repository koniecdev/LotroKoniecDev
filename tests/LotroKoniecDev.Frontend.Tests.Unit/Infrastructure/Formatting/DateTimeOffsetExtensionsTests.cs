using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.Formatting;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Formatting;

/// <summary>
/// Every visible date goes through here (#736): both sides of the daylight-saving switch, a day change,
/// and an input whose own offset is neither UTC nor Poland's.
/// </summary>
public sealed class DateTimeOffsetExtensionsTests
{
    [Theory]
    // Summer: Europe/Warsaw is CEST, UTC+2.
    [InlineData("2026-08-24T21:48:00Z", "2026-08-24 23:48 czasu polskiego")]
    // Winter: Europe/Warsaw is CET, UTC+1.
    [InlineData("2026-12-10T10:00:00Z", "2026-12-10 11:00 czasu polskiego")]
    // Poland is already on the next day, so raw UTC is wrong even for a date alone.
    [InlineData("2026-08-24T22:30:00Z", "2026-08-25 00:30 czasu polskiego")]
    // The last minute before the spring-forward jump, and the first one after it.
    [InlineData("2026-03-29T00:59:00Z", "2026-03-29 01:59 czasu polskiego")]
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29 03:00 czasu polskiego")]
    // The seconds are cut, never rounded, like the auth e-mails and login page do (#890).
    [InlineData("2026-08-18T22:30:59.999Z", "2026-08-19 00:30 czasu polskiego")]
    // An input carrying its own non-UTC offset still converts by instant.
    [InlineData("2026-08-24T16:48:00-05:00", "2026-08-24 23:48 czasu polskiego")]
    public void ToPolandTimeText_RendersTheInstantInPolandTimeWithTheZoneNamed(string instant, string expected)
    {
        DateTimeOffset value = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        value.ToPolandTimeText().ShouldBe(expected);
    }

    [Fact]
    public void ToPolandTimeTextWithoutZone_OmitsTheZoneForProseThatNamesItAlready()
    {
        DateTimeOffset value = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

        value.ToPolandTimeTextWithoutZone().ShouldBe("2026-07-25 12:00");
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
