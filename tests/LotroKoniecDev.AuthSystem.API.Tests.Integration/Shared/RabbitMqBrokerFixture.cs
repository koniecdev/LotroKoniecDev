using DotNet.Testcontainers.Containers;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.Tests.Shared;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// One real RabbitMQ container per test class. The rest of the suite runs without a broker, behind
/// <see cref="SpyMessagePublisher"/>, on purpose. But dead-letter routing, the retry counting of a
/// quorum queue and the delivery limit are things the broker does, so only a real broker can show that
/// the topology we declare really has them.
/// </summary>
public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder(RabbitMqImage.Name)
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

    /// <summary>
    /// The container's coordinates as the very settings shape production binds from
    /// configuration, so a test can construct the real publisher against this broker.
    /// </summary>
    public RabbitMqOptions BuildOptions()
    {
        Uri amqpUri = new(_container.GetConnectionString());
        string[] userInfo = amqpUri.UserInfo.Split(':');

        return new RabbitMqOptions
        {
            Host = amqpUri.Host,
            Port = amqpUri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = Uri.UnescapeDataString(userInfo[1]),
            VirtualHost = amqpUri.AbsolutePath.Length > 1
                ? Uri.UnescapeDataString(amqpUri.AbsolutePath.TrimStart('/'))
                : "/"
        };
    }

    /// <summary>
    /// Closes every client connection from the broker side. This is the restart a long-lived publisher
    /// has to survive by opening a new channel on its next publish.
    /// </summary>
    public async Task CloseAllConnectionsAsync()
    {
        ExecResult result = await _container.ExecAsync(
            ["rabbitmqctl", "close_all_connections", "integration-test induced failure"]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"rabbitmqctl close_all_connections failed (exit code {result.ExitCode}).\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        }
    }
}
