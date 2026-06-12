using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
namespace LotroKoniecDev.AuthSystem.API.ExceptionHandlers;

internal sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    private readonly IProblemDetailsService _problemDetailsService;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
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
        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status500InternalServerError,
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/500",
            Title = "Sorry, an internal server error has occurred, there is nothing You can do.",
            Extensions = { ["errorCode"] = "Internal.UnhandledException" }
        };

        if (_environment.IsEnvironment("Testing") || _environment.IsEnvironment("Development"))
        {
            problemDetails.Detail = exception.Message;
            problemDetails.Extensions["exceptionType"] = exception.GetType().Name;
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
        }

        LogGlobalException(_logger, exception, exception.GetType().Name, "Exception", exception.Message);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    [LoggerMessage(EventId = EventIds.UnhandledException, Level = LogLevel.Error, Message = "Unhandled exception: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogGlobalException(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
