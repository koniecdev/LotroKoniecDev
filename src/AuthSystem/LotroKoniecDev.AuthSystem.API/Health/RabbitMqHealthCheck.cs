using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Proves the broker accepts an AMQP connection with the configured credentials — a full handshake
/// rather than a bare TCP connect, so a wrong password or virtual host surfaces here instead of
/// only in the relay's retry logs. Deliberately NOT tagged "ready" (same reasoning as
/// <see cref="SmtpHealthCheck"/>): a broker outage degrades e-mail delivery gracefully — outbox
/// rows wait, the consumer reconnects with backoff — while login and token issuance keep working,
/// so it must not pull the service out of the ingress rotation. A down broker surfaces on the full
/// /health, which the daily health ping probes.
/// </summary>
internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqOptions _settings;

    public RabbitMqHealthCheck(IOptions<RabbitMqOptions> options)
    {
        _settings = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ConnectionFactory connectionFactory =
                RabbitMqConnectionFactoryBuilder.Build(_settings, "lotro-auth-api-health-check");

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await using IConnection connection = await connectionFactory.CreateConnectionAsync(cts.Token);

            return HealthCheckResult.Healthy($"Broker reachable at {_settings.Host}:{_settings.Port}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Broker unreachable at {_settings.Host}:{_settings.Port}",
                exception: ex);
        }
    }
}
