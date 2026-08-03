using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// One real RabbitMQ container per test class — the rest of the suite deliberately runs
/// broker-less behind <see cref="SpyMessagePublisher"/>, but dead-letter routing, quorum-queue
/// redelivery counting and the delivery limit are broker behavior: only the broker itself can
/// prove our declared topology actually has them.
/// </summary>
public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    /// <summary>
    /// Pinned to the exact compose.yaml version (management UI stripped — dead weight in a
    /// test), so the suite proves the topology against the broker generation the stack actually
    /// runs. compose.yaml points back here: a bump must touch both.
    /// </summary>
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.3.4-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public async Task<IConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectionFactory connectionFactory = new()
        {
            Uri = new Uri(_container.GetConnectionString())
        };

        return await connectionFactory.CreateConnectionAsync(cancellationToken);
    }
}
