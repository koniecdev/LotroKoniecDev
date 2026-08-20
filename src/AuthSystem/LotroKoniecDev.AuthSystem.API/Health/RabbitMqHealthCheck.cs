using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Health;

/// <summary>
/// Checks that the broker accepts an AMQP connection with the configured credentials. It does a full
/// handshake and not just a TCP connect, so a wrong password or virtual host shows up here instead of
/// only in the relay's retry logs.
/// It is not tagged "ready" on purpose, for the same reason as <see cref="SmtpHealthCheck"/>. When the
/// broker is down, e-mail delivery slows but nothing breaks: outbox rows wait and the consumer
/// reconnects. Login and token issuance keep working, so this must not take the service out of the
/// load balancer. A broker that is down shows up on the full /health, which the daily health ping
/// reads.
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
