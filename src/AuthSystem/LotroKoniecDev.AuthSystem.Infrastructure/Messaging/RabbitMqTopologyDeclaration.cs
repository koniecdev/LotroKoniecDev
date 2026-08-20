using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Declares the exchanges, queues and bindings of <see cref="RabbitMqTopology"/>. Declaring the same
/// thing twice in AMQP is safe, so both the publisher and the consumer run this when they open a
/// channel: whichever starts first creates the topology and the other one does nothing.
/// The publisher needs it because a topic exchange drops messages that reach no bound queue without
/// saying so. The consumer needs it because consuming from a queue nobody declared yet fails.
/// </summary>
/// <remarks>
/// This is only safe to repeat while the arguments stay the same. Declaring an existing queue with
/// different arguments fails the channel with <c>PRECONDITION_FAILED</c>. So changing anything in
/// <see cref="EmailQueueArguments"/> means deleting the queue first, which in dev is
/// <c>docker compose down -v</c>. See ADR-0036.
/// </remarks>
public static class RabbitMqTopologyDeclaration
{
    /// <summary>
    /// <see cref="RabbitMqTopology.EmailQueue"/> is a quorum queue, so the broker counts retries
    /// itself in <c>x-delivery-count</c> and applies
    /// <see cref="RabbitMqTopology.EmailDeliveryLimit"/>. A classic queue does neither, and every
    /// consumer would have to keep that count instead.
    /// Messages that are rejected or run out of retries go to
    /// <see cref="RabbitMqTopology.EmailsDeadLetterExchange"/>. The <c>at-least-once</c> strategy makes
    /// that step as safe as the publish that brought the message in; the default <c>at-most-once</c>
    /// can drop dead letters under load. That strategy requires <c>x-overflow: reject-publish</c>,
    /// which does nothing here because no <c>x-max-length</c> is set and the queue never overflows.
    /// </summary>
    private static readonly Dictionary<string, object?> EmailQueueArguments = new()
    {
        ["x-queue-type"] = "quorum",
        ["x-dead-letter-exchange"] = RabbitMqTopology.EmailsDeadLetterExchange,
        ["x-delivery-limit"] = RabbitMqTopology.EmailDeliveryLimit,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish"
    };

    /// <summary>
    /// The dead-letter queue is a quorum queue too, so it is as durable as the queue it backs up. It
    /// has no dead-letter exchange of its own and no explicit delivery limit, because it is the end of
    /// the line. The quorum <em>default</em> limit of 20 still applies, so a replay must ack the
    /// message and publish it again. Rejecting and requeueing in a loop would quietly lose it
    /// (ADR-0036).
    /// </summary>
    private static readonly Dictionary<string, object?> DeadLetterQueueArguments = new()
    {
        ["x-queue-type"] = "quorum"
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // The dead-letter side comes first. That queue must exist before anything can be routed to
        // it, or a dead letter in the gap would reach an exchange with no queue bound to it.
        await channel.ExchangeDeclareAsync(
            exchange: RabbitMqTopology.EmailsDeadLetterExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: RabbitMqTopology.EmailDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: DeadLetterQueueArguments,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: RabbitMqTopology.EmailDeadLetterQueue,
            exchange: RabbitMqTopology.EmailsDeadLetterExchange,
            routingKey: string.Empty,
            arguments: null,
            cancellationToken: cancellationToken);

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
            arguments: EmailQueueArguments,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: RabbitMqTopology.EmailQueue,
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailBindingPattern,
            arguments: null,
            cancellationToken: cancellationToken);
    }
}
