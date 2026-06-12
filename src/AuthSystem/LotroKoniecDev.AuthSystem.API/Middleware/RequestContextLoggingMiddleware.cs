using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

internal sealed class RequestContextLoggingMiddleware
{
    private const string CorrelationIdHeaderName = "X-Correlation-ID";
    private const string CorrelationIdItemName = "CorrelationId";
    private readonly RequestDelegate _next;

    public RequestContextLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = GetOrCreateCorrelationId(context);

        context.Items[CorrelationIdItemName] = correlationId;
        context.Response.Headers.TryAdd(CorrelationIdHeaderName, correlationId);

        using (LogContext.PushProperty(CorrelationIdItemName, correlationId))
        {
            await _next(context);
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out StringValues existingId)
            && !string.IsNullOrWhiteSpace(existingId))
        {
            return existingId.ToString();
        }

        return context.TraceIdentifier;
    }
}
