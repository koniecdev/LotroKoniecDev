using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Declares the exchange, the queue and the binding of <see cref="RabbitMqTopology"/>. AMQP
/// declarations are idempotent, so both the publisher and the consumer run the same declaration
/// on channel open: whichever side starts first creates the topology, the other becomes a no-op.
/// The publisher needs it because a topic exchange silently drops messages that reach no bound
/// queue; the consumer needs it because consuming from a queue that nobody declared yet fails.
/// </summary>
public static class RabbitMqTopologyDeclaration
{
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: RabbitMqTopology.EmailsExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: RabbitMqTopology.EmailQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: RabbitMqTopology.EmailQueue,
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailBindingPattern,
            arguments: null,
            cancellationToken: cancellationToken);
    }
}
