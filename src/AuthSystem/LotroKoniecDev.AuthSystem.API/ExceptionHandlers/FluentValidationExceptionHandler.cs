using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
namespace LotroKoniecDev.AuthSystem.API.ExceptionHandlers;

internal sealed partial class FluentValidationExceptionHandler : IExceptionHandler
{
    private readonly ILogger<FluentValidationExceptionHandler> _logger;
    private readonly IProblemDetailsService _problemDetailsService;

    public FluentValidationExceptionHandler(
        ILogger<FluentValidationExceptionHandler> logger,
        IProblemDetailsService problemDetailsService)
    {
        _logger = logger;
        _problemDetailsService = problemDetailsService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
        {
            return false;
        }

        Dictionary<string, string[]> errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        ValidationProblemDetails problemDetails = new(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/400",
            Title = "Validation Error",
            Extensions = { ["errorCode"] = "Validation.FluentValidation" }
        };

        LogValidationException(_logger, validationException, validationException.GetType().Name, "ValidationException", validationException.Message);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    [LoggerMessage(EventId = EventIds.ValidationFailure, Level = LogLevel.Warning, Message = "Validation failure: {ErrorCode} ({ErrorType}) — {ErrorMessage}")]
    private static partial void LogValidationException(ILogger logger, Exception exception, string errorCode, string errorType, string errorMessage);
}
