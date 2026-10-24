using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// One factory shape for every broker client here, so the publisher and the consumer recover the same
/// way. They hold separate connections because under load AMQP throttles a publishing connection at the
/// TCP level, and a shared socket would stop the consumer too.
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
