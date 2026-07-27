using LotroKoniecDev.Frontend.Infrastructure.Security;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Security;

/// <summary>
/// Guards the login challenge, the local sign-out and the cookie-consent bounce-back — every route
/// that redirects to a caller-supplied target. Twin of the auth server's <c>LocalReturnUrlTests</c>:
/// the two guards must stay behaviourally identical.
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
    /// Browsers strip ASCII tab and newline before parsing a URL, so
    /// <c>Location: /&lt;tab&gt;/evil.example</c> is followed as the protocol-relative
    /// <c>//evil.example</c> — a prefix-only check would call that value local.
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
