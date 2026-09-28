using System.Text;
using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// Every error answer of the auth origin goes through this writer, so it decides for all of them whether a
/// person reads Polish or raw JSON, and which Polish page fits the status (#867, #879).
/// </summary>
public sealed class BrowserErrorPageWriterTests
{
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status405MethodNotAllowed)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void CanWrite_ShouldBeTrue_WhenABrowserGetsAnError(int statusCode)
    {
        // Arrange
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, statusCode);

        // Act
        bool canWrite = writer.CanWrite(context);

        // Assert
        canWrite.ShouldBeTrue();
    }

    [Theory]
    [InlineData("application/vnd.dev-lotrokoniecdev.hateoas.json")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData(null)]
    public void CanWrite_ShouldBeFalse_WhenAnApiClientAsks(string? accept)
    {
        // Arrange: the frontend's back-channel sends the first value, and curl or fetch send */*
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(accept, StatusCodes.Status404NotFound);

        // Act
        bool canWrite = writer.CanWrite(context);

        // Assert
        canWrite.ShouldBeFalse();
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK)]
    [InlineData(StatusCodes.Status302Found)]
    public void CanWrite_ShouldBeFalse_WhenTheStatusIsNotAnError(int statusCode)
    {
        // Arrange
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, statusCode);

        // Act
        bool canWrite = writer.CanWrite(context);

        // Assert
        canWrite.ShouldBeFalse();
    }

    [Fact]
    public async Task WriteAsync_ShouldWriteTheNotFoundPage_WhenTheStatusIs404()
    {
        // Arrange
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status404NotFound);

        // Act
        await writer.WriteAsync(context);

        // Assert
        context.HttpContext.Response.ContentType.ShouldBe("text/html; charset=utf-8");
        string html = ReadBody(context);
        html.ShouldContain("<h1>Nie ma takiej strony</h1>");
        html.ShouldContain(BrowserErrorPage.BackToLoginLink);
    }

    [Fact]
    public async Task WriteAsync_ShouldWriteTheFormExpiredPage_WhenTheAntiforgeryCheckFailed()
    {
        // Arrange
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status400BadRequest);
        MarkAntiforgeryFailure(context.HttpContext);

        // Act
        await writer.WriteAsync(context);

        // Assert
        string html = ReadBody(context);
        html.ShouldContain("<h1>Formularz wygasł</h1>");
        html.ShouldContain("odśwież stronę i wyślij go jeszcze raz");
        html.ShouldContain("nie blokuje ciasteczek");
        html.ShouldContain(BrowserErrorPage.BackToLoginLink);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status405MethodNotAllowed)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status422UnprocessableEntity)]
    public async Task WriteAsync_ShouldWriteTheGeneralPage_ForAnyOther4xx(int statusCode)
    {
        // Arrange: a 400 without the antiforgery mark is not an expired form
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, statusCode);

        // Act
        await writer.WriteAsync(context);

        // Assert
        string html = ReadBody(context);
        html.ShouldContain("<h1>Nie udało się obsłużyć żądania</h1>");
        html.ShouldNotContain("Formularz wygasł");
        html.ShouldContain(BrowserErrorPage.BackToLoginLink);
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status502BadGateway)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task WriteAsync_ShouldWriteTheServerErrorPage_ForAny5xx(int statusCode)
    {
        // Arrange
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, statusCode);

        // Act
        await writer.WriteAsync(context);

        // Assert
        ReadBody(context).ShouldContain("<h1>Coś poszło nie tak</h1>");
    }

    [Fact]
    public async Task WriteAsync_ShouldAskTheUserToWait_WhenAnEndpointsOwnBudgetAnswers429()
    {
        // Arrange: the general page would invite an immediate retry, which the budget refuses again
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status429TooManyRequests);

        // Act
        await writer.WriteAsync(context);

        // Assert
        string html = ReadBody(context);
        html.ShouldContain("<h1>Za dużo prób</h1>");
        html.ShouldContain("Odczekaj chwilę i spróbuj ponownie.");
    }

    [Fact]
    public async Task WriteAsync_ShouldNotShowTheProblemDetails_WhenTheyCarryTheException()
    {
        // Arrange: in Development and Testing the details carry the exception's message and stack trace
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status500InternalServerError);
        context.ProblemDetails.Title = "Sorry, an internal server error has occurred";
        context.ProblemDetails.Detail = "Npgsql.PostgresException: relation does not exist";
        context.ProblemDetails.Extensions["stackTrace"] = "at LotroKoniecDev.AuthSystem.API.Secret()";

        // Act
        await writer.WriteAsync(context);

        // Assert
        string html = ReadBody(context);
        html.ShouldNotContain("internal server error");
        html.ShouldNotContain("PostgresException");
        html.ShouldNotContain("Secret()");
    }

    [Fact]
    public async Task WriteAsync_ShouldPutTheRequestsNonceOnItsStyleBlock()
    {
        // Arrange: the auth origin's CSP admits an inline style only with the request's nonce (#693)
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status404NotFound);
        string nonce = CspNonce.Issue(context.HttpContext);

        // Act
        await writer.WriteAsync(context);

        // Assert
        ReadBody(context).ShouldContain($"<style nonce=\"{nonce}\">");
    }

    [Fact]
    public async Task WriteAsync_ShouldNotThrow_WhenTheBrowserHasAlreadyGone()
    {
        // Arrange: a long database timeout, and the user closed the tab before it ended. A throw here
        // would log the same failure two more times.
        BrowserErrorPageWriter writer = new();
        ProblemDetailsContext context = BuildContext(BrowserAccept, StatusCodes.Status500InternalServerError);
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        context.HttpContext.RequestAborted = aborted.Token;

        // Act
        await writer.WriteAsync(context);

        // Assert
        ReadBody(context).ShouldContain("<h1>Coś poszło nie tak</h1>");
    }

    private static ProblemDetailsContext BuildContext(string? accept, int statusCode)
    {
        DefaultHttpContext httpContext = new();
        if (accept is not null)
        {
            httpContext.Request.Headers.Accept = accept;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.Body = new MemoryStream();

        return new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = statusCode }
        };
    }

    private static void MarkAntiforgeryFailure(HttpContext httpContext)
    {
        ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());
        new AntiforgeryFailureFilter().OnResultExecuting(
            new ResultExecutingContext(actionContext, [], new AntiforgeryValidationFailedResult(), controller: new object()));
    }

    private static string ReadBody(ProblemDetailsContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.HttpContext.Response.Body).ToArray());
}
