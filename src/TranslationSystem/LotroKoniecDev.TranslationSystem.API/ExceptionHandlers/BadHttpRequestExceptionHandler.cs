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

        // So an admin uploading too large an exported.txt is told so, not just "Bad Request" (#208).
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
    /// Kestrel's body limit throws a 413, but the multipart limit throws an
    /// <see cref="InvalidDataException"/> that minimal-API binding wraps in a 400. This treats every
    /// wrapped form-read error as too large, which holds only while the import is the one form endpoint.
    /// Revisit it before adding another, where key, value or count limits are real 400s.
    /// </summary>
    private static bool IsPayloadTooLarge(BadHttpRequestException exception)
        => exception.StatusCode == StatusCodes.Status413PayloadTooLarge
           || exception.InnerException is InvalidDataException
           || exception.InnerException is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge };

    [LoggerMessage(EventId = EventIds.BadHttpRequest, Level = LogLevel.Warning, Message = "Bad HTTP request: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogBadHttpRequest(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
