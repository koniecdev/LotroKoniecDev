using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.PwnedPasswords;

public sealed class PwnedPasswordsTelemetryTests
{
    [Theory]
    [InlineData("https://api.pwnedpasswords.com/range/5BAA6", true)]
    [InlineData("https://API.PwnedPasswords.com/range/5BAA6", true)]
    [InlineData("https://api.pwnedpasswords.com/", true)]
    [InlineData("https://auth.lotro-translator.pl/connect/token", false)]
    [InlineData("https://api.pwnedpasswords.com.evil.example/range/5BAA6", false)]
    [InlineData("https://haveibeenpwned.com/range/5BAA6", false)]
    public void IsRangeApiRequest_ForEachRequestUri_PicksOnlyTheRangeApiHost(string requestUri, bool expected)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(requestUri));

        bool isRangeApiRequest = PwnedPasswordsTelemetry.IsRangeApiRequest(request);

        isRangeApiRequest.ShouldBe(expected);
    }

    [Fact]
    public void IsRangeApiRequest_ForARelativeUri_ReturnsFalse()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("range/5BAA6", UriKind.Relative));

        bool isRangeApiRequest = PwnedPasswordsTelemetry.IsRangeApiRequest(request);

        isRangeApiRequest.ShouldBeFalse();
    }
}
