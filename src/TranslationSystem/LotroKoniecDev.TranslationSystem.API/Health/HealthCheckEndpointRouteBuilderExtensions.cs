using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace LotroKoniecDev.TranslationSystem.API.Health;

internal static class HealthCheckEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the checks the way <c>MapHealthChecks</c> does, but with <see cref="HealthCheckKeyMiddleware"/>
    /// in front of them inside the same endpoint (ADR-0058). The key check therefore always runs first and
    /// cannot be left out by a pipeline change. The endpoint is anonymous because the key, not a login,
    /// is what admits the caller; without it the fallback policy would answer 401 first.
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
