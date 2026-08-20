using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Builds the one <see cref="ConnectionFactory"/> shape every broker client in this process uses, so
/// the publisher and the consumer cannot end up recovering differently.
/// They hold separate connections on purpose, told apart by <paramref name="clientProvidedName"/>.
/// Under load AMQP slows a publishing connection down at the TCP level, and if both directions shared
/// one socket that would also stop the consumer.
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
