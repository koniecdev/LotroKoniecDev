using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LotroKoniecDev.AuthSystem.API.Health;

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
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                duration = e.Value.Duration.ToString(),
                description = e.Value.Description,
                exception = e.Value.Exception?.Message,
                data = e.Value.Data.Count > 0 ? e.Value.Data : null
            })
        };

        await context.Response.WriteAsJsonAsync(response, JsonOptions);
    }
}
