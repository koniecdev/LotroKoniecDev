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
    /// A quote or an angle bracket inside a path is still a path, so the check keeps the value. It only
    /// decides whether a target is local, and this copy is only ever redirected to, never printed. Kept in
    /// step with the twin (#681).
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""")]
    [InlineData("/x<script>alert(1)</script>")]
    public void Sanitize_WhenALocalPathCarriesHtmlCharacters_KeepsItVerbatim(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBe(returnUrl);
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
