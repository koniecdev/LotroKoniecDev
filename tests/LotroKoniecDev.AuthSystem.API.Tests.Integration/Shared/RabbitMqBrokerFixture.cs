using DotNet.Testcontainers.Containers;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.Tests.Shared;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// One real RabbitMQ container per test class. Dead-letter routing, quorum retry counting and the delivery
/// limit are broker behaviour, so only a real broker shows that our topology has them.
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
    /// In the settings shape production binds, so a test can build the real publisher against this broker.
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
        await RunRabbitMqCtlAsync("close_all_connections", "integration-test induced failure");
    }

    /// <summary>
    /// Sets a queue operator policy with the same <c>rabbitmqctl set_operator_policy</c> arguments that
    /// <c>scripts/hetzner/deploy.sh</c> uses on a box. It runs as root, which is safe here because the
    /// broker has fully started and written its Erlang cookie.
    /// </summary>
    public async Task SetOperatorPolicyAsync(string name, string pattern, string definition)
    {
        await RunRabbitMqCtlAsync("set_operator_policy", "--apply-to", "queues", name, pattern, definition);
    }

    private async Task RunRabbitMqCtlAsync(params string[] arguments)
    {
        ExecResult result = await _container.ExecAsync(["rabbitmqctl", .. arguments]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"rabbitmqctl {arguments[0]} failed (exit code {result.ExitCode}).\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        }
    }
}
