using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// The auth pages put <c>returnUrl</c> into their own links and redirect to it after a successful
/// sign-in, so anything this check lets through becomes an open redirect on the auth origin, which is
/// the most valuable phishing target we have.
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
    /// A quote or an angle bracket inside a path is still a path, so the check keeps the value. It is not
    /// an HTML escaper: the pages that print it HTML-encode it themselves (#681). Pinned here so nobody
    /// reads this check as the escaping step and drops the encoding somewhere else.
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""")]
    [InlineData("/x<script>alert(1)</script>")]
    public void Sanitize_WhenALocalPathCarriesHtmlCharacters_KeepsItVerbatim(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBe(returnUrl);
    }

    /// <summary>
    /// Browsers drop ASCII tab and newline before they parse a URL, so <c>/&lt;tab&gt;/evil.example</c>
    /// reads as <c>//evil.example</c> and must never end up in a link on the page. It arrives as
    /// <c>%09</c> in the query string, and model binding has already decoded it by the time the check
    /// sees it.
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
