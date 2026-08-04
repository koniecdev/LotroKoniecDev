using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Builds the one <see cref="ConnectionFactory"/> shape every broker client in this process uses,
/// so recovery behavior cannot drift between the publisher and the consumer. Publisher and
/// consumer deliberately hold separate connections (distinguished by
/// <paramref name="clientProvidedName"/>): AMQP applies TCP back-pressure to a publishing
/// connection under load, which would stall consuming if both directions shared one socket.
/// </summary>
public static class RabbitMqConnectionFactoryBuilder
{
    public static ConnectionFactory Build(RabbitMqOptions settings, string clientProvidedName) =>
        new()
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.Username,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = clientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
}
