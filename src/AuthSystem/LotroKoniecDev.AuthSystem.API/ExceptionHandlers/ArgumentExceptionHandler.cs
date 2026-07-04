using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LotroKoniecDev.AuthSystem.API.ExceptionHandlers;

internal sealed partial class ArgumentExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ArgumentExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IProblemDetailsService _problemDetailsService;

    public ArgumentExceptionHandler(
        ILogger<ArgumentExceptionHandler> logger,
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
        if (exception is not ArgumentException argumentException)
        {
            return false;
        }

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/400",
            Title = "Bad Request",
            Detail = "One or more arguments provided are invalid.",
            Extensions = { ["errorCode"] = "Http.InvalidArgument" }
        };

        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            problemDetails.Detail = argumentException.Message;
        }

        LogArgumentException(_logger, argumentException, argumentException.GetType().Name, "ArgumentException", argumentException.Message);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    [LoggerMessage(EventId = EventIds.ArgumentException, Level = LogLevel.Warning, Message = "Argument exception: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogArgumentException(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
