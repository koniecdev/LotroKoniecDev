using System.Diagnostics;
using LotroKoniecDev.Domain.Core.Monads;
using Mediator;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Behaviors;

public sealed class RequestLoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IMessage
    where TResponse : Result
{
    private readonly ILogger<RequestLoggingPipelineBehavior<TRequest, TResponse>> _logger;

    public RequestLoggingPipelineBehavior(ILogger<RequestLoggingPipelineBehavior<TRequest, TResponse>> logger)
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
            _logger.LogError(
                "Completed request {RequestName} in {Duration}ms with error {@Error}",
                requestName,
                duration,
                result.Error);
        }

        _logger.LogInformation(
            "Completed request {RequestName} in {Duration}ms",
            requestName,
            duration);

        return result;
    }
}
