using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// Who gets an error page and who keeps the machine-readable answer. A wrong call either shows a person
/// raw JSON, or hands the frontend's back-channel HTML it cannot parse.
/// </summary>
public sealed class BrowserErrorPageTests
{
    [Theory]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML")]
    public void WantsHtml_ShouldBeTrue_WhenTheCallerNamesHtml(string accept)
    {
        // Arrange
        DefaultHttpContext context = new();
        context.Request.Headers.Accept = accept;

        // Act
        bool wantsHtml = BrowserErrorPage.WantsHtml(context.Request);

        // Assert
        wantsHtml.ShouldBeTrue();
    }

    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("*/*")]
    [InlineData("")]
    public void WantsHtml_ShouldBeFalse_WhenTheCallerDoesNotNameHtml(string accept)
    {
        // Arrange: the frontend's clients send the first value, curl and fetch send */*
        DefaultHttpContext context = new();
        context.Request.Headers.Accept = accept;

        // Act
        bool wantsHtml = BrowserErrorPage.WantsHtml(context.Request);

        // Assert
        wantsHtml.ShouldBeFalse();
    }

    [Fact]
    public void WantsHtml_ShouldBeFalse_WhenThereIsNoAcceptHeader()
    {
        // Arrange
        DefaultHttpContext context = new();

        // Act
        bool wantsHtml = BrowserErrorPage.WantsHtml(context.Request);

        // Assert
        wantsHtml.ShouldBeFalse();
    }
}
