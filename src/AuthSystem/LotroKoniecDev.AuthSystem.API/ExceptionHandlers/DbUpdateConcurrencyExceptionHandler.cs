using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace LotroKoniecDev.AuthSystem.API.ExceptionHandlers;

internal sealed partial class DbUpdateConcurrencyExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DbUpdateConcurrencyExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IProblemDetailsService _problemDetailsService;

    public DbUpdateConcurrencyExceptionHandler(
        ILogger<DbUpdateConcurrencyExceptionHandler> logger,
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
        if (exception is not DbUpdateConcurrencyException concurrencyException)
        {
            return false;
        }

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status409Conflict,
            Type = "https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/409",
            Title = "Concurrency conflict",
            Detail = "The resource was modified by another request. Please refresh and try again.",
            Extensions = { ["errorCode"] = "Db.ConcurrencyConflict" }
        };

        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            problemDetails.Detail = concurrencyException.Message;
        }

        LogConcurrencyConflict(_logger, concurrencyException, concurrencyException.Message);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    [LoggerMessage(EventId = EventIds.ConcurrencyConflict, Level = LogLevel.Warning, Message = "Concurrency conflict: {Message}")]
    private static partial void LogConcurrencyConflict(ILogger logger, Exception exception, string message);
}
