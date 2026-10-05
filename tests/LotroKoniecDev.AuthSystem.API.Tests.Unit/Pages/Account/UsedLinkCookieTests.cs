using LotroKoniecDev.AuthSystem.API.Pages.Account;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The marker a page leaves in the browser that used a one-time link, so Back to the link's form shows
/// "done" instead of a button that calls the link dead (#941, ADR-0063).
/// </summary>
public sealed class UsedLinkCookieTests
{
    private const string Token = "CfDJ8Kx+used/link==";

    [Fact]
    public void WasUsedHere_TheTokenThisBrowserUsed_ReturnsTrue()
    {
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(UsedLinkCookie.PasswordReset, Token));

        bool used = UsedLinkCookie.PasswordReset.WasUsedHere(request, Token);

        used.ShouldBeTrue();
    }

    [Theory]
    [InlineData("CfDJ8Kx+other/link==")]
    [InlineData("cfdj8kx+used/link==")]
    [InlineData("CfDJ8Kx+used/link== ")]
    [InlineData("")]
    public void WasUsedHere_AnyOtherToken_ReturnsFalse(string token)
    {
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(UsedLinkCookie.PasswordReset, Token));

        bool used = UsedLinkCookie.PasswordReset.WasUsedHere(request, token);

        used.ShouldBeFalse();
    }

    [Fact]
    public void WasUsedHere_NoCookie_ReturnsFalse()
    {
        HttpRequest request = new DefaultHttpContext().Request;

        bool used = UsedLinkCookie.PasswordReset.WasUsedHere(request, Token);

        used.ShouldBeFalse();
    }

    [Fact]
    public void WasUsedHere_TheMarkerOfTheOtherFlow_ReturnsFalse()
    {
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(UsedLinkCookie.EmailChangeConfirm, Token));

        bool used = UsedLinkCookie.PasswordReset.WasUsedHere(request, Token);

        used.ShouldBeFalse();
    }

    /// <summary>
    /// The cookie holds a hash, so a cookie carrying the token itself, which anyone can send, is no marker.
    /// </summary>
    [Theory]
    [InlineData(Token)]
    [InlineData("1")]
    [InlineData("")]
    public void WasUsedHere_ACookieThatIsNotTheTokensHash_ReturnsFalse(string cookieValue)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(UsedLinkCookie.PasswordReset.Name, Uri.EscapeDataString(cookieValue)).ToString();

        bool used = UsedLinkCookie.PasswordReset.WasUsedHere(httpContext.Request, Token);

        used.ShouldBeFalse();
    }

    [Fact]
    public void Remember_ShouldNotStoreTheTokenItself()
    {
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(UsedLinkCookie.EmailChangeConfirm, Token);

        string value = cookie.Value.ToString();
        value.ShouldNotContain(Token);
        value.ShouldNotContain(Uri.EscapeDataString(Token));
    }

    [Fact]
    public void Remember_ShouldWriteAShortLivedCookieNoScriptCanRead()
    {
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(UsedLinkCookie.EmailChangeConfirm, Token);

        cookie.Name.ToString().ShouldBe(".lotrokoniecdev.used-link.email-change");
        cookie.HttpOnly.ShouldBeTrue();
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        cookie.Path.ToString().ShouldBe("/");
        cookie.MaxAge.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Remember_ShouldMarkTheCookieSecureExactlyWhenTheRequestIsHttps(bool isHttps)
    {
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(UsedLinkCookie.PasswordReset, Token, isHttps);

        cookie.Secure.ShouldBe(isHttps);
    }

    private static SetCookieHeaderValue SetCookieAfterRemembering(
        UsedLinkCookie usedLinkCookie, string token, bool isHttps = true)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.IsHttps = isHttps;

        usedLinkCookie.Remember(httpContext, token);

        return SetCookieHeaderValue.Parse(httpContext.Response.Headers.SetCookie.ToString());
    }

    private static HttpRequest RequestCarrying(SetCookieHeaderValue cookie)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(cookie.Name, cookie.Value).ToString();
        return httpContext.Request;
    }
}
