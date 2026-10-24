using LotroKoniecDev.Frontend.Infrastructure.Security;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Security;

/// <summary>
/// The twin of the auth server's <c>LocalReturnUrlTests</c>; the two must behave the same.
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
    /// The check only decides whether a target is local; this value is redirected to, never printed (#681).
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""")]
    [InlineData("/x<script>alert(1)</script>")]
    public void Sanitize_WhenALocalPathCarriesHtmlCharacters_KeepsItVerbatim(string returnUrl)
    {
        LocalReturnUrl.Sanitize(returnUrl).ShouldBe(returnUrl);
    }

    /// <summary>
    /// Browsers drop tab and newline, so <c>/&lt;tab&gt;/evil.example</c> is followed as
    /// <c>//evil.example</c>.
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
