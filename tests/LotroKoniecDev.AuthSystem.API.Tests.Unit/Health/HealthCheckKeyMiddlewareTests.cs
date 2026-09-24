using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.AuthSystem.API.Health;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Health;

/// <summary>
/// The gate in front of the full /health (ADR-0058, #853). The next delegate stands in for the health
/// checks. It answers 202, a status a fresh context never has, so a 202 means the checks ran and a 404
/// means they never started.
/// </summary>
public sealed class HealthCheckKeyMiddlewareTests
{
    private const string ConfiguredKey = "a-health-check-key-of-at-least-32-characters";
    private const int ChecksRanStatus = StatusCodes.Status202Accepted;

    [Fact]
    public async Task InvokeAsync_WithTheConfiguredKey_RunsTheChecks()
    {
        // Arrange
        HealthCheckKeyMiddleware middleware = CreateMiddleware(ConfiguredKey);
        DefaultHttpContext context = new();
        context.Request.Headers[HealthCheckHeaders.Key] = ConfiguredKey;

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(ChecksRanStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData(ConfiguredKey + " ")]
    [InlineData(" " + ConfiguredKey)]
    [InlineData("A-HEALTH-CHECK-KEY-OF-AT-LEAST-32-CHARACTERS")]
    [InlineData("a-health-check-key-of-at-least-32-character")]
    public async Task InvokeAsync_WithAnyOtherKey_Returns404WithoutRunningTheChecks(string presentedKey)
    {
        // Arrange: the cases with a space pin the exact compare; over HTTP, Kestrel trims them first
        HealthCheckKeyMiddleware middleware = CreateMiddleware(ConfiguredKey);
        DefaultHttpContext context = new();
        context.Request.Headers[HealthCheckHeaders.Key] = presentedKey;

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task InvokeAsync_WithoutTheHeader_Returns404WithoutRunningTheChecks()
    {
        // Arrange
        HealthCheckKeyMiddleware middleware = CreateMiddleware(ConfiguredKey);
        DefaultHttpContext context = new();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task InvokeAsync_WithTheKeySentTwice_Returns404WithoutRunningTheChecks()
    {
        // Arrange: a repeated header is ambiguous, so it is refused even when every copy is right
        HealthCheckKeyMiddleware middleware = CreateMiddleware(ConfiguredKey);
        DefaultHttpContext context = new();
        context.Request.Headers[HealthCheckHeaders.Key] = new[] { ConfiguredKey, ConfiguredKey };

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeAsync_WithNoKeyConfigured_RunsTheChecksForAnyCaller(string? configuredKey)
    {
        // Arrange: only Development and Testing may boot without a key (HealthCheckSettingsValidator)
        HealthCheckKeyMiddleware middleware = CreateMiddleware(configuredKey);
        DefaultHttpContext context = new();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(ChecksRanStatus);
    }

    private static HealthCheckKeyMiddleware CreateMiddleware(string? configuredKey)
    {
        return new HealthCheckKeyMiddleware(
            context =>
            {
                context.Response.StatusCode = ChecksRanStatus;
                return Task.CompletedTask;
            },
            Microsoft.Extensions.Options.Options.Create(new HealthCheckSettings { Key = configuredKey }));
    }
}
