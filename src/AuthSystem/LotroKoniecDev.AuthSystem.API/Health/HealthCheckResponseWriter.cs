using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Only name, status and duration (ADR-0058): a failed check's description can hold a host, port or user
/// name, and the daily health ping prints this body into a public job log. The application log keeps
/// the full detail.
/// </summary>
internal static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                duration = entry.Value.Duration.ToString()
            })
        };

        await context.Response.WriteAsJsonAsync(response, JsonOptions);
    }
}
