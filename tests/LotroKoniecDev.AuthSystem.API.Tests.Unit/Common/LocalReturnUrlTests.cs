using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// Anything this check lets through becomes an open redirect on the auth origin, the most valuable phishing
/// target we have.
/// </summary>
public sealed class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/translations?status=NeedsReview")]
    [InlineData("/connect/authorize?client_id=web&redirect_uri=https%3A%2F%2Fapp.example%2Fcallback")]
    public void Sanitize_WhenTargetIsALocalPath_KeepsItVerbatim(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBe(returnUrl);
    }

    [Theory]
    [InlineData("https://evil.example/harvest")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("dashboard")]
    [InlineData(" /dashboard")]
    [InlineData("~/dashboard")]
    [InlineData("")]
    [InlineData(null)]
    public void Sanitize_WhenTargetIsNotALocalPath_DropsIt(string? returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBeNull();
    }

    /// <summary>
    /// The check is not an HTML escaper: the pages encode the value themselves (#681). Pinned so nobody takes
    /// the check for the escaping step and drops the encoding.
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""")]
    [InlineData("/x<script>alert(1)</script>")]
    public void Sanitize_WhenALocalPathCarriesHtmlCharacters_KeepsItVerbatim(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBe(returnUrl);
    }

    /// <summary>
    /// Browsers drop tab and newline before they parse a URL, so <c>/&lt;tab&gt;/evil.example</c> reads as
    /// <c>//evil.example</c>. It arrives as <c>%09</c>, already decoded by model binding.
    /// </summary>
    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r\n/evil.example")]
    [InlineData("/\t\\evil.example")]
    public void Sanitize_WhenTargetHidesBehindControlCharacters_DropsIt(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBeNull();
    }
}
