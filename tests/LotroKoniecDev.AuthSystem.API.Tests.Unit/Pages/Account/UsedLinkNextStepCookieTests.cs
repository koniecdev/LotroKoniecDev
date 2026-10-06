using LotroKoniecDev.AuthSystem.API.Pages.Account;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The cookie that sends a used undo or cancel link on to the password form it opened the first time
/// (#941, ADR-0063). It carries a live reset token, so it must be unreadable and unforgeable.
/// </summary>
public sealed class UsedLinkNextStepCookieTests
{
    private const string UsedToken = "CfDJ8Kx+used/undo==";
    private static readonly PasswordResetStep NextStep = new("frodo@shire.me", "CfDJ8Kx+fresh/reset==");

    private readonly UsedLinkNextStepCookie _cookie = new(new EphemeralDataProtectionProvider());

    [Theory]
    [InlineData(nameof(UsedLinkFlow.EmailChangeRevert))]
    [InlineData(nameof(UsedLinkFlow.DeletionCancel))]
    public void NextStepFor_TheLinkThisBrowserUsed_ReturnsTheStepItWasSentTo(string flowName)
    {
        UsedLinkFlow flow = Enum.Parse<UsedLinkFlow>(flowName);
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(_cookie, flow, UsedToken));

        PasswordResetStep? step = _cookie.NextStepFor(request, flow, UsedToken);

        step.ShouldBe(NextStep);
    }

    [Theory]
    [InlineData("CfDJ8Kx+other/undo==")]
    [InlineData("cfdj8kx+used/undo==")]
    [InlineData("")]
    public void NextStepFor_AnyOtherLink_ReturnsNull(string token)
    {
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(_cookie, UsedLinkFlow.EmailChangeRevert, UsedToken));

        PasswordResetStep? step = _cookie.NextStepFor(request, UsedLinkFlow.EmailChangeRevert, token);

        step.ShouldBeNull();
    }

    [Fact]
    public void NextStepFor_NoCookie_ReturnsNull()
    {
        PasswordResetStep? step = _cookie.NextStepFor(new DefaultHttpContext().Request, UsedLinkFlow.DeletionCancel, UsedToken);

        step.ShouldBeNull();
    }

    /// <summary>
    /// Each flow has its own key purpose, so the undo cookie copied under the cancel cookie's name opens
    /// nothing.
    /// </summary>
    [Fact]
    public void NextStepFor_TheOtherFlowsValueUnderThisFlowsName_ReturnsNull()
    {
        SetCookieHeaderValue revertCookie = SetCookieAfterRemembering(_cookie, UsedLinkFlow.EmailChangeRevert, UsedToken);
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(
            UsedLinkNextStepCookie.NameOf(UsedLinkFlow.DeletionCancel), revertCookie.Value).ToString();

        PasswordResetStep? step = _cookie.NextStepFor(httpContext.Request, UsedLinkFlow.DeletionCancel, UsedToken);

        step.ShouldBeNull();
    }

    [Fact]
    public void NextStepFor_AValueMadeWithAnotherKeyring_ReturnsNull()
    {
        UsedLinkNextStepCookie otherServer = new(new EphemeralDataProtectionProvider());
        HttpRequest request = RequestCarrying(SetCookieAfterRemembering(otherServer, UsedLinkFlow.EmailChangeRevert, UsedToken));

        PasswordResetStep? step = _cookie.NextStepFor(request, UsedLinkFlow.EmailChangeRevert, UsedToken);

        step.ShouldBeNull();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("%%%")]
    public void NextStepFor_AValueThatDoesNotDecrypt_ReturnsNull(string value)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(
            UsedLinkNextStepCookie.NameOf(UsedLinkFlow.EmailChangeRevert), Uri.EscapeDataString(value)).ToString();

        PasswordResetStep? step = _cookie.NextStepFor(httpContext.Request, UsedLinkFlow.EmailChangeRevert, UsedToken);

        step.ShouldBeNull();
    }

    [Fact]
    public void Remember_ShouldKeepTheResetTokenAndTheAddressOutOfTheCookieText()
    {
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(_cookie, UsedLinkFlow.DeletionCancel, UsedToken);

        string value = Uri.UnescapeDataString(cookie.Value.ToString());
        value.ShouldNotContain("fresh");
        value.ShouldNotContain("frodo");
    }

    [Theory]
    [InlineData(nameof(UsedLinkFlow.EmailChangeRevert), ".lotrokoniecdev.used-link.email-change-revert")]
    [InlineData(nameof(UsedLinkFlow.DeletionCancel), ".lotrokoniecdev.used-link.deletion-cancel")]
    public void Remember_ShouldWriteAShortLivedCookieNoScriptCanRead(string flowName, string expectedName)
    {
        UsedLinkFlow flow = Enum.Parse<UsedLinkFlow>(flowName);
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(_cookie, flow, UsedToken);

        cookie.Name.ToString().ShouldBe(expectedName);
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
        SetCookieHeaderValue cookie = SetCookieAfterRemembering(_cookie, UsedLinkFlow.EmailChangeRevert, UsedToken, isHttps);

        cookie.Secure.ShouldBe(isHttps);
    }

    private static SetCookieHeaderValue SetCookieAfterRemembering(
        UsedLinkNextStepCookie usedLinkNextStepCookie, UsedLinkFlow flow, string usedToken, bool isHttps = true)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.IsHttps = isHttps;

        usedLinkNextStepCookie.Remember(httpContext, flow, usedToken, NextStep);

        return SetCookieHeaderValue.Parse(httpContext.Response.Headers.SetCookie.ToString());
    }

    private static HttpRequest RequestCarrying(SetCookieHeaderValue cookie)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = new CookieHeaderValue(cookie.Name, cookie.Value).ToString();
        return httpContext.Request;
    }
}
