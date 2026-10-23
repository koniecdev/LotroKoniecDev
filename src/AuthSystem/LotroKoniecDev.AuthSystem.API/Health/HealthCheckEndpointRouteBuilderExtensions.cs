using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace LotroKoniecDev.AuthSystem.API.Health;

internal static class HealthCheckEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Like <c>MapHealthChecks</c>, but with <see cref="HealthCheckKeyMiddleware"/> inside the same
    /// endpoint, so a pipeline change cannot skip the key (ADR-0058). Anonymous, because the key admits
    /// the caller, not a login.
    /// </summary>
    public static IEndpointConventionBuilder MapKeyGatedHealthChecks(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        HealthCheckOptions options)
    {
        RequestDelegate pipeline = endpoints.CreateApplicationBuilder()
            .UseMiddleware<HealthCheckKeyMiddleware>()
            .UseMiddleware<HealthCheckMiddleware>(Microsoft.Extensions.Options.Options.Create(options))
            .Build();

        return endpoints.Map(pattern, pipeline)
            .WithDisplayName("Health checks (key required)")
            .AllowAnonymous();
    }
}
