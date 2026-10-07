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

    /// <summary>
    /// A second change in the same half hour must not make Back to the first link's page call it dead again.
    /// </summary>
    [Fact]
    public void WasUsedHere_AnEarlierLinkAfterASecondOneWasUsed_ReturnsTrue()
    {
        HttpRequest request = RequestCarrying(SetCookieAfterRememberingInOrder(UsedLinkCookie.EmailChangeConfirm, "first-link", "second-link"));

        bool firstUsed = UsedLinkCookie.EmailChangeConfirm.WasUsedHere(request, "first-link");
        bool secondUsed = UsedLinkCookie.EmailChangeConfirm.WasUsedHere(request, "second-link");

        firstUsed.ShouldBeTrue();
        secondUsed.ShouldBeTrue();
    }

    [Fact]
    public void WasUsedHere_AfterSixLinks_ForgetsOnlyTheOldest()
    {
        string[] tokens = ["link-1", "link-2", "link-3", "link-4", "link-5", "link-6"];
        HttpRequest request = RequestCarrying(SetCookieAfterRememberingInOrder(UsedLinkCookie.PasswordReset, tokens));

        bool[] used = tokens.Select(token => UsedLinkCookie.PasswordReset.WasUsedHere(request, token)).ToArray();

        used.ShouldBe([false, true, true, true, true, true]);
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

    private static SetCookieHeaderValue SetCookieAfterRememberingInOrder(UsedLinkCookie usedLinkCookie, params string[] tokens)
    {
        SetCookieHeaderValue? cookie = null;
        foreach (string token in tokens)
        {
            DefaultHttpContext httpContext = new();
            httpContext.Request.IsHttps = true;
            if (cookie is not null)
            {
                httpContext.Request.Headers.Cookie = new CookieHeaderValue(cookie.Name, cookie.Value).ToString();
            }

            usedLinkCookie.Remember(httpContext, token);
            cookie = SetCookieHeaderValue.Parse(httpContext.Response.Headers.SetCookie.ToString());
        }

        return cookie ?? throw new ArgumentException("At least one token is needed.", nameof(tokens));
    }

    private static HttpRequest RequestCarrying(SetCookieHeaderValue cookie)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(cookie.Name, cookie.Value).ToString();
        return httpContext.Request;
    }
}
