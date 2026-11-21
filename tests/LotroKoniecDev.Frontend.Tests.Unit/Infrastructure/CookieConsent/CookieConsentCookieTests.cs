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
    public void WasShown_TreatsAnyNonBlankValueAsShown(string? cookieValue, bool expected)
    {
        CookieConsentCookie.WasShown(cookieValue).ShouldBe(expected);
    }
}
