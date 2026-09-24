using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Writes each check's name, status and duration, and nothing else (ADR-0058, #853). A failed check's
/// description and exception can hold a connection error with a host, a port or a user name: the Npgsql
/// check copies the exception message into its description, and so does the health check service when a
/// check throws. The daily health ping prints this body into a public job log. The full detail is in
/// the application log, where the health check service writes every failed check at Error level.
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
