using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LotroKoniecDev.AuthSystem.API.Health;

internal sealed class SmtpHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public SmtpHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string? host = _configuration["Email:Host"];
        int? port = _configuration.GetValue<int?>("Email:Port");

        if (string.IsNullOrWhiteSpace(host) || port is null)
        {
            return HealthCheckResult.Unhealthy("SMTP configuration is missing (Email:Host and/or Email:Port)");
        }

        try
        {
            using TcpClient client = new();
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port.Value, cts.Token);

            return HealthCheckResult.Healthy($"SMTP server reachable at {host}:{port}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"SMTP server unreachable at {host}:{port}",
                exception: ex);
        }
    }
}
