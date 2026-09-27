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
        bool written = await ServerErrorPage.WriteIfBrowserRequestAsync(context);

        // Assert
        written.ShouldBeTrue();
        context.Response.ContentType.ShouldBe("text/html; charset=utf-8");
        string html = ReadBody(context);
        html.ShouldContain("<h1>Coś poszło nie tak</h1>");
        html.ShouldContain("Spróbuj ponownie za chwilę.");
        html.ShouldContain(BrowserErrorPage.BackToLoginLink);
    }

    [Fact]
    public async Task WriteIfBrowserRequestAsync_ShouldWriteNothing_WhenAnApiClientAsks()
    {
        // Arrange: the frontend's back-channel must keep the problem details it can parse
        DefaultHttpContext context = BuildContext("application/vnd.dev-lotrokoniecdev.hateoas.json");

        // Act
        bool written = await ServerErrorPage.WriteIfBrowserRequestAsync(context);

        // Assert
        written.ShouldBeFalse();
        context.Response.ContentType.ShouldBeNull();
        ReadBody(context).ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteIfBrowserRequestAsync_ShouldNotThrow_WhenTheBrowserHasAlreadyGone()
    {
        // Arrange: a long database timeout, and the user closed the tab before it ended. A throw here
        // would log the same failure two more times.
        DefaultHttpContext context = BuildContext(BrowserAccept);
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        // Act
        bool written = await ServerErrorPage.WriteIfBrowserRequestAsync(context);

        // Assert
        written.ShouldBeTrue();
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
