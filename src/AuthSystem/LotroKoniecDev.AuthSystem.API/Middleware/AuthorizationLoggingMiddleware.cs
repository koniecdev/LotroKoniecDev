using System.Net;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Names two addresses: the visitor the frontend forwarded with the right key, or else the connection's
/// address, and then the connection's own address. The second one stays because a leaked frontend key
/// could choose the first (ADR-0054, amended by #854).
/// </summary>
internal sealed partial class AuthorizationLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitPartitionKeyResolver _clientResolver;
    private readonly ILogger<AuthorizationLoggingMiddleware> _logger;

    public AuthorizationLoggingMiddleware(
        RequestDelegate next,
        RateLimitPartitionKeyResolver clientResolver,
        ILogger<AuthorizationLoggingMiddleware> logger)
    {
        _next = next;
        _clientResolver = clientResolver;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        // Only a refused call to a real endpoint gets a warning. OpenIddict checks the client of a GET
        // to connect/introspect too, but only POST is routed there, so that call lands on routing's 405
        // endpoint, which is not a RouteEndpoint and carries no rate limit. Scanners send such calls
        // without end. The request log still records them.
        if (context.GetEndpoint() is not RouteEndpoint)
        {
            return;
        }

        switch (context.Response.StatusCode)
        {
            case StatusCodes.Status401Unauthorized:
                LogUnauthorizedAccess(_logger,
                    context.Request.Method,
                    context.Request.Path,
                    _clientResolver.ResolveClientAddress(context),
                    context.Connection.RemoteIpAddress);
                break;
            case StatusCodes.Status403Forbidden:
                LogForbiddenAccess(_logger,
                    context.Request.Method,
                    context.Request.Path,
                    _clientResolver.ResolveClientAddress(context),
                    context.Connection.RemoteIpAddress,
                    context.User.Identity?.Name ?? "anonymous");
                break;
        }
    }

    [LoggerMessage(EventId = EventIds.UnauthorizedAccessAttempt, Level = LogLevel.Warning, Message = "Unauthorized access attempt: {Method} {Path} from {Client} via {IP}")]
    private static partial void LogUnauthorizedAccess(ILogger logger, string method, PathString path, IPAddress? client, IPAddress? ip);

    [LoggerMessage(EventId = EventIds.ForbiddenAccessAttempt, Level = LogLevel.Warning, Message = "Forbidden access attempt: {Method} {Path} from {Client} via {IP} by {User}")]
    private static partial void LogForbiddenAccess(ILogger logger, string method, PathString path, IPAddress? client, IPAddress? ip, string user);
}
