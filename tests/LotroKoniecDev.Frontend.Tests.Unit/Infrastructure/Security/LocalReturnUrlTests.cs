using LotroKoniecDev.Frontend.Infrastructure.Security;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Security;

/// <summary>
/// Covers the login challenge, the local sign-out and the cookie-consent bounce-back, which are every
/// route that redirects to a target the caller supplied. It is the twin of the auth server's
/// <c>LocalReturnUrlTests</c>, and the two must behave identically.
/// </summary>
public sealed class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/translations?status=NeedsReview")]
    [InlineData("/account/deletion-scheduled?until=2026-07-25T10%3A00%3A00Z")]
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
    /// Browsers drop ASCII tab and newline before they parse a URL, so
    /// <c>Location: /&lt;tab&gt;/evil.example</c> is followed as <c>//evil.example</c>. A check that only
    /// looked at the first characters would call that value local.
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
