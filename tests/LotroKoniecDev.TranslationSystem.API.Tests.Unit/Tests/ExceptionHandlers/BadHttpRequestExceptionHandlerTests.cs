using System.Text.Json;
using LotroKoniecDev.TranslationSystem.API.ExceptionHandlers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.ExceptionHandlers;

/// <summary>
/// Pins the bad-request handler's status normalization (#208): an over-limit upload — however the
/// framework surfaces it — is reported as a clean 413, while every other bad request stays 400 and a
/// non-bad-request exception is left for the next handler.
/// </summary>
public sealed class BadHttpRequestExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ForKestrelRequestBodyTooLarge_ShouldReturn413()
    {
        BadHttpRequestException exception = new("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        (bool handled, DefaultHttpContext context, ProblemDetails? problemDetails) = await HandleAsync(exception);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        problemDetails.ShouldNotBeNull();
        problemDetails.Status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        problemDetails.Extensions["errorCode"].ShouldBe("Http.PayloadTooLarge");
    }

    [Fact]
    public async Task TryHandleAsync_ForMultipartLengthLimitWrappedAsBadRequest_ShouldReturn413()
    {
        // The multipart form-length cap throws InvalidDataException, which minimal-API binding wraps as
        // a generic 400 — normalized here to a clean 413.
        BadHttpRequestException exception = new(
            "Failed to read parameter \"file\" from the request body as form.",
            StatusCodes.Status400BadRequest,
            new InvalidDataException("Multipart body length limit 65536 exceeded."));

        (bool handled, DefaultHttpContext context, ProblemDetails? problemDetails) = await HandleAsync(exception);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        problemDetails.ShouldNotBeNull();
        problemDetails.Extensions["errorCode"].ShouldBe("Http.PayloadTooLarge");
    }

    [Fact]
    public async Task TryHandleAsync_ForKestrel413WrappedDuringFormRead_ShouldReturn413()
    {
        // Kestrel's request-body cap (413) thrown while the form reader pulls the body, wrapped as a 400.
        BadHttpRequestException exception = new(
            "Failed to read parameter \"file\" from the request body as form.",
            StatusCodes.Status400BadRequest,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        (bool handled, DefaultHttpContext context, ProblemDetails? problemDetails) = await HandleAsync(exception);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        problemDetails.ShouldNotBeNull();
        problemDetails.Extensions["errorCode"].ShouldBe("Http.PayloadTooLarge");
    }

    [Fact]
    public async Task TryHandleAsync_ForOrdinaryBadRequest_ShouldStay400()
    {
        // A malformed JSON body is a genuine 400 and must NOT be relabeled "too large" — the regression
        // guard for the over-broad payload-too-large detection.
        BadHttpRequestException exception = new(
            "Failed to read parameter \"command\" from the request body as JSON.",
            StatusCodes.Status400BadRequest,
            new JsonException("Unexpected end of JSON input."));

        (bool handled, DefaultHttpContext context, ProblemDetails? problemDetails) = await HandleAsync(exception);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problemDetails.ShouldNotBeNull();
        problemDetails.Extensions["errorCode"].ShouldBe("Http.BadRequest");
    }

    [Fact]
    public async Task TryHandleAsync_ForNonBadHttpRequestException_ShouldNotHandle()
    {
        (bool handled, DefaultHttpContext context, ProblemDetails? problemDetails) =
            await HandleAsync(new InvalidOperationException("boom"));

        handled.ShouldBeFalse();
        problemDetails.ShouldBeNull();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    private static async Task<(bool Handled, DefaultHttpContext Context, ProblemDetails? ProblemDetails)> HandleAsync(
        Exception exception)
    {
        ProblemDetails? captured = null;
        IProblemDetailsService problemDetailsService = Substitute.For<IProblemDetailsService>();
        problemDetailsService
            .When(service => service.WriteAsync(Arg.Any<ProblemDetailsContext>()))
            .Do(call => captured = call.Arg<ProblemDetailsContext>().ProblemDetails);

        // Production environment so the handler keeps its clean ProblemDetails rather than enriching it
        // with the raw exception message / stack trace (which it does only in Development/Testing).
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        BadHttpRequestExceptionHandler handler = new(
            NullLogger<BadHttpRequestExceptionHandler>.Instance,
            environment,
            problemDetailsService);

        DefaultHttpContext context = new();
        bool handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        return (handled, context, captured);
    }
}
