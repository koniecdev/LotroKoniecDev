using System.Text;
using System.Text.Json;
using LotroKoniecDev.TranslationSystem.API.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Health;

/// <summary>
/// The health report leaves out every text a failed check carries (ADR-0058, #853). The daily health
/// ping prints this body into a public job log, and a connection error can name a host, a port or a user.
/// </summary>
public sealed class HealthCheckResponseWriterTests
{
    private const string DescriptionText = "Failed to connect to 10.0.0.5:5432 as user lotro_owner";
    private const string ExceptionText = "password authentication failed for user lotro_owner";
    private const string DataValue = "db.internal.example";

    [Fact]
    public async Task WriteResponse_WithAFailedCheck_WritesNameStatusAndDurationOnly()
    {
        // Arrange
        HealthReport report = new(
            new Dictionary<string, HealthReportEntry>
            {
                ["translationdb"] = new(
                    HealthStatus.Unhealthy,
                    DescriptionText,
                    TimeSpan.FromMilliseconds(12),
                    new InvalidOperationException(ExceptionText),
                    new Dictionary<string, object> { ["host"] = DataValue })
            },
            TimeSpan.FromMilliseconds(15));
        DefaultHttpContext context = new();
        using MemoryStream body = new();
        context.Response.Body = body;

        // Act
        await HealthCheckResponseWriter.WriteResponse(context, report);

        // Assert
        string json = Encoding.UTF8.GetString(body.ToArray());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement check = document.RootElement.GetProperty("checks").EnumerateArray().Single();
        check.EnumerateObject().Select(property => property.Name).ShouldBe(["name", "status", "duration"]);
        check.GetProperty("name").GetString().ShouldBe("translationdb");
        check.GetProperty("status").GetString().ShouldBe("Unhealthy");
        json.ShouldNotContain(DescriptionText);
        json.ShouldNotContain(ExceptionText);
        json.ShouldNotContain(DataValue);
    }
}
