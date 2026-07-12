using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.CookieConsent;

public sealed class CookieConsentCookieTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("true", true)]
    [InlineData("anything", true)]
    public void IsAccepted_TreatsAnyNonBlankValueAsConsent(string? cookieValue, bool expected)
    {
        CookieConsentCookie.IsAccepted(cookieValue).ShouldBe(expected);
    }
}
