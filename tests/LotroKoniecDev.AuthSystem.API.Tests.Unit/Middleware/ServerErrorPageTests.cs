using System.Text;
using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

public sealed class ServerErrorPageTests
{
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    [Fact]
    public async Task WriteIfBrowserRequestAsync_ShouldWriteThePolishPage_WhenABrowserAsks()
    {
        // Arrange
        DefaultHttpContext context = BuildContext(BrowserAccept);

        // Act
        bool written = await ServerErrorPage.WriteIfBrowserRequestAsync(context, CancellationToken.None);

        // Assert
        written.ShouldBeTrue();
        context.Response.ContentType.ShouldBe("text/html; charset=utf-8");
        string html = ReadBody(context);
        html.ShouldContain("<h1>Coś poszło nie tak</h1>");
        html.ShouldContain("Spróbuj ponownie za chwilę.");
        html.ShouldContain("<a href=\"/Account/Login\">Wróć do logowania</a>");
    }

    [Fact]
    public async Task WriteIfBrowserRequestAsync_ShouldWriteNothing_WhenAnApiClientAsks()
    {
        // Arrange: the frontend's back-channel must keep the problem details it can parse
        DefaultHttpContext context = BuildContext("application/vnd.dev-lotrokoniecdev.hateoas.json");

        // Act
        bool written = await ServerErrorPage.WriteIfBrowserRequestAsync(context, CancellationToken.None);

        // Assert
        written.ShouldBeFalse();
        context.Response.ContentType.ShouldBeNull();
        ReadBody(context).ShouldBeEmpty();
    }

    [Fact]
    public void BuildHtml_ShouldPutTheRequestsNonceOnItsStyleBlock()
    {
        // Act: the auth origin's CSP admits an inline style only with the request's nonce (#693)
        string html = ServerErrorPage.BuildHtml("r4nd0m-n0nce_value");

        // Assert
        html.ShouldContain("<style nonce=\"r4nd0m-n0nce_value\">");
        html.ShouldNotContain("<style>");
        html.ShouldNotContain("<script");
    }

    [Fact]
    public void BuildHtml_ShouldLeaveTheNonceOut_WhenThereIsNoCsp()
    {
        // Act: Development runs without the security headers, so there is no nonce to match
        string html = ServerErrorPage.BuildHtml(nonce: null);

        // Assert
        html.ShouldContain("<style>");
        html.ShouldNotContain("nonce");
    }

    private static DefaultHttpContext BuildContext(string accept)
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Accept = accept;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(DefaultHttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}
