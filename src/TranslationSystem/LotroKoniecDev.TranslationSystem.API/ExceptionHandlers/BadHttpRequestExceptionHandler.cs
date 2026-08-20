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

        // An upload that is too large arrives in two different shapes. Kestrel's request-body limit
        // throws a 413, while the multipart form limit throws an InvalidDataException that minimal-API
        // binding turns into a plain 400. We turn both into a 413, so an admin who uploads too large an
        // exported.txt is told the file is too large instead of just "Bad Request" (#208).
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
    /// True when the bad request is really an upload that is too large and should become a 413. That is
    /// either a 413 straight from Kestrel's request-body limit, or a wrapped
    /// <see cref="InvalidDataException"/> or wrapped 413, because the multipart form reader's limit
    /// throws an <see cref="InvalidDataException"/> that minimal-API binding turns into a 400.
    /// <para>
    /// The <see cref="InvalidDataException"/> case turns <em>every</em> wrapped form-read error into a
    /// 413, not only the size one. That is fine today, because the import upload is the API's only
    /// endpoint that accepts a form, and with the framework's default limits the only failure it can
    /// realistically hit is the body-length one. Look at this check again before adding another form
    /// endpoint, where the other limits on key, value or count could be hit for real. Those are proper
    /// 400s and not "too large".
    /// </para>
    /// </summary>
    private static bool IsPayloadTooLarge(BadHttpRequestException exception)
        => exception.StatusCode == StatusCodes.Status413PayloadTooLarge
           || exception.InnerException is InvalidDataException
           || exception.InnerException is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge };

    [LoggerMessage(EventId = EventIds.BadHttpRequest, Level = LogLevel.Warning, Message = "Bad HTTP request: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogBadHttpRequest(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
