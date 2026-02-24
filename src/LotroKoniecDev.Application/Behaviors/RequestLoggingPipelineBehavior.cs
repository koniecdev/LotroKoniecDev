using System.Diagnostics;
using LotroKoniecDev.Domain.Core.Monads;
using Mediator;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace LotroKoniecDev.Application.Behaviors;

public sealed class RequestLoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> 
    where TRequest : IMessage
    where TResponse : Result
{
    private readonly ILogger<TRequest> _logger;

    public RequestLoggingPipelineBehavior(ILogger<TRequest> logger)
    {
        _logger = logger;
    }
    
    public async ValueTask<TResponse> Handle(
        TRequest message,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        string requestName = typeof(TRequest).Name;
        
        _logger.LogInformation(
            "Processing request {RequestName}",
            requestName);

        Stopwatch stopwatch = Stopwatch.StartNew();
        
        TResponse result = await next(message, cancellationToken);
        
        stopwatch.Stop();
        
        long duration = stopwatch.ElapsedMilliseconds;
        
        if (result.IsFailure)
        {
            using (LogContext.PushProperty("Error", result.Error, true))
            {
                _logger.LogError(
                    "Completed request {RequestName} in {Duration}ms with error",
                    requestName,
                    duration);
            }
        }
        
        _logger.LogInformation(
            "Completed request {RequestName} in {Duration}ms",
            requestName, 
            duration);
        
        return result;
    }
}
