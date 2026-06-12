using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.AuthSystem.API.ExceptionHandlers;

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

        ProblemDetails problemDetails = new()
        {
            Status = badHttpRequestException.StatusCode,
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/400",
            Title = "Bad Request",
            Extensions = { ["errorCode"] = "Http.BadRequest" }
        };

        if (_environment.IsEnvironment("Testing") || _environment.IsEnvironment("Development"))
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

    [LoggerMessage(EventId = EventIds.BadHttpRequest, Level = LogLevel.Warning, Message = "Bad HTTP request: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogBadHttpRequest(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
