using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.ExceptionHandlers;

internal sealed partial class BadHttpRequestExceptionHandler : IExceptionHandler
{
    private readonly ILogger<BadHttpRequestExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IProblemDetailsService _problemDetailsService;

    public BadHttpRequestExceptionHandler(
        ILogger<BadHttpRequestExceptionHandler> logger,
        IHostEnvironment environment,
        IProblemDetailsService problemDetailsService)
    {
        _logger = logger;
        _environment = environment;
        _problemDetailsService = problemDetailsService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badHttpRequestException)
        {
            return false;
        }

        // An oversized upload surfaces inconsistently — Kestrel's request-body cap throws a 413, while
        // the multipart form-length cap throws an InvalidDataException that minimal-API binding wraps as
        // a generic 400. Normalize both to a clean 413 so an admin uploading too large an exported.txt
        // gets an actionable "too large" response rather than a bare "Bad Request" (#208).
        ProblemDetails problemDetails = IsPayloadTooLarge(badHttpRequestException)
            ? new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/413",
                Title = "Payload Too Large",
                Detail = "The upload exceeds the maximum allowed size.",
                Extensions = { ["errorCode"] = "Http.PayloadTooLarge" }
            }
            : new ProblemDetails
            {
                Status = badHttpRequestException.StatusCode,
                Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/400",
                Title = "Bad Request",
                Extensions = { ["errorCode"] = "Http.BadRequest" }
            };

        if (_environment.IsTesting() || _environment.IsDevelopment())
        {
            problemDetails.Detail = badHttpRequestException.Message;
            problemDetails.Extensions["exceptionType"] = badHttpRequestException.GetType().Name;
            problemDetails.Extensions["stackTrace"] = badHttpRequestException.StackTrace;
        }

        LogBadHttpRequest(_logger, badHttpRequestException, badHttpRequestException.GetType().Name, "BadHttpRequestException", badHttpRequestException.Message);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    /// <summary>
    /// True when the bad request is really an over-limit upload, to be normalized to 413: a direct 413
    /// from Kestrel's request-body cap, or — because the multipart form reader's length cap throws an
    /// <see cref="InvalidDataException"/> that minimal-API binding wraps as a 400 — a wrapped
    /// <see cref="InvalidDataException"/> or wrapped 413.
    /// <para>
    /// The <see cref="InvalidDataException"/> arm maps <em>every</em> wrapped form-read data error to
    /// 413, not only the size one. That is acceptable today because the version-bound import upload is
    /// the API's only form-accepting endpoint and, with the framework's default form limits, its sole
    /// realistic form-read failure is the body-length (size) cap. Revisit this predicate before adding
    /// another form endpoint whose other form limits (key/value/count) could legitimately trip — those
    /// are genuine 400s, not "too large".
    /// </para>
    /// </summary>
    private static bool IsPayloadTooLarge(BadHttpRequestException exception)
        => exception.StatusCode == StatusCodes.Status413PayloadTooLarge
           || exception.InnerException is InvalidDataException
           || exception.InnerException is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge };

    [LoggerMessage(EventId = EventIds.BadHttpRequest, Level = LogLevel.Warning, Message = "Bad HTTP request: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogBadHttpRequest(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
