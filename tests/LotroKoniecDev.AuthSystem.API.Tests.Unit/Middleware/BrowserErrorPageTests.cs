using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// Who gets an error page and who keeps the machine-readable answer, and the frame every error page
/// shares. A wrong call either shows a person raw JSON, or hands the frontend's back-channel HTML it
/// cannot parse.
/// </summary>
public sealed class BrowserErrorPageTests
{
    [Theory]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML")]
    [InlineData("application/json, text/html;q=0.5")]
    public void WantsHtml_ShouldBeTrue_WhenTheCallerAcceptsHtml(string accept)
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
    [InlineData("application/json, text/html;q=0")]
    public void WantsHtml_ShouldBeFalse_WhenTheCallerDoesNotAcceptHtml(string accept)
    {
        // Arrange: the frontend's clients send the first value, curl and fetch send */*, and q=0 refuses HTML
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

    [Fact]
    public void BuildHtml_ShouldLoadItsStylesFromAFile()
    {
        // Act: the auth CSP's style-src is 'self', so an inline style would be blocked (#833)
        string html = BrowserErrorPage.BuildHtml("Nagłówek", "Akapit.");

        // Assert
        html.ShouldContain("<link rel=\"stylesheet\" href=\"/error-page.css\" />");
        html.ShouldNotContain("<style");
        html.ShouldNotContain(" style=");
    }

    [Fact]
    public void BuildHtml_ShouldCarryNoScript()
    {
        // Act: the CSP's script-src is 'self', so an inline script would be blocked
        string html = BrowserErrorPage.BuildHtml("Nagłówek", "Akapit.");

        // Assert
        html.ShouldNotContain("<script");
    }

    [Fact]
    public void BuildHtml_ShouldPutTheHeadingAndEachParagraphOnThePage()
    {
        // Act
        string html = BrowserErrorPage.BuildHtml("Nagłówek", "Pierwszy.", "Drugi.");

        // Assert
        html.ShouldContain("<title>Nagłówek — lotro-translator.pl</title>");
        html.ShouldContain("<h1>Nagłówek</h1>");
        html.ShouldContain("<p>Pierwszy.</p>");
        html.ShouldContain("<p>Drugi.</p>");
    }
}
