using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace LotroKoniecDev.TranslationSystem.API.Health;

internal static class HealthCheckEndpointRouteBuilderExtensions
{
    /// <summary>
    /// The key check sits inside the same endpoint, so no pipeline change can leave it out (ADR-0058).
    /// Anonymous because the key, not a login, admits the caller.
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
