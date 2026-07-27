using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// The auth pages reflect <c>returnUrl</c> into their own links and redirect to it after a
/// successful sign-in, so anything this guard lets through becomes an open redirect on the auth
/// origin — the highest-value phishing target in the stack.
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
    /// Browsers strip ASCII tab and newline before parsing, so <c>/&lt;tab&gt;/evil.example</c> reads
    /// as the protocol-relative <c>//evil.example</c> — it must never be reflected into a link on the
    /// page. Arriving as <c>%09</c> in the query string, the value is already decoded by model
    /// binding when it reaches the guard.
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
