using System.Net;
using System.Security.Claims;
using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// No real token gets a 403 from the auth API today, so the integration suite reaches the 403 warning only
/// through a test host that gives a client token this API's audience (#1023). This pins its wording
/// without that seam (#854); the TMS integration suite proves the same line end to end. The rule that a
/// refused call with no real endpoint is not warned is pinned here as well: the integration suite proved
/// it with a GET to connect/introspect until #900 made that call a 400.
/// </summary>
public sealed class AuthorizationLoggingMiddlewareTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string FrontendKey = new('k', 40);

    [Fact]
    public async Task InvokeAsync_WhenARealEndpointForbidsAFrontendCall_ShouldNameTheVisitorTheConnectionAndTheUser()
    {
        // Arrange
        CapturingLogger<AuthorizationLoggingMiddleware> logger = new();
        AuthorizationLoggingMiddleware middleware = CreateMiddleware(StatusCodes.Status403Forbidden, logger);
        DefaultHttpContext context = CreateContext(HttpMethods.Post, "/auth/account/change-email", "10.60.0.7");
        context.Request.Headers.Append(FrontendCallerHeaders.Key, FrontendKey);
        context.Request.Headers.Append(FrontendCallerHeaders.ClientAddress, "203.0.113.5");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "anna")], "Bearer"));
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("auth/account/change-email"),
            0,
            EndpointMetadataCollection.Empty,
            "change-email"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        CapturingLogger<AuthorizationLoggingMiddleware>.LogEntry warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.EventId.ShouldBe(EventIds.ForbiddenAccessAttempt);
        warning.Message.ShouldBe("Forbidden access attempt: POST /auth/account/change-email from 203.0.113.5 via 10.60.0.7 by anna");
    }

    [Fact]
    public async Task InvokeAsync_WhenACallThatMatchesNoRouteIsRefused_ShouldNotWarn()
    {
        // Arrange: a call with no real endpoint carries no rate limit, so scanners could add warnings
        // there without end.
        CapturingLogger<AuthorizationLoggingMiddleware> logger = new();
        AuthorizationLoggingMiddleware middleware = CreateMiddleware(StatusCodes.Status401Unauthorized, logger);
        DefaultHttpContext context = CreateContext(HttpMethods.Get, "/no-such-path", "203.0.113.6");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WhenACallOnRoutingsMethodNotAllowedEndpointIsRefused_ShouldNotWarn()
    {
        // Arrange: routing's 405 endpoint is a plain Endpoint, not a RouteEndpoint, and carries no rate limit
        CapturingLogger<AuthorizationLoggingMiddleware> logger = new();
        AuthorizationLoggingMiddleware middleware = CreateMiddleware(StatusCodes.Status401Unauthorized, logger);
        DefaultHttpContext context = CreateContext(HttpMethods.Get, "/auth/change-password", "203.0.113.7");
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            EndpointMetadataCollection.Empty,
            "405 HTTP Method Not Supported"));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        logger.Entries.ShouldBeEmpty();
    }

    private static AuthorizationLoggingMiddleware CreateMiddleware(
        int statusCode,
        CapturingLogger<AuthorizationLoggingMiddleware> logger)
    {
        return new AuthorizationLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            new RateLimitPartitionKeyResolver(
                Microsoft.Extensions.Options.Options.Create(new FrontendCallerSettings { Key = FrontendKey })),
            logger);
    }

    private static DefaultHttpContext CreateContext(string method, string path, string connectionAddress)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(connectionAddress);
        return context;
    }
}
