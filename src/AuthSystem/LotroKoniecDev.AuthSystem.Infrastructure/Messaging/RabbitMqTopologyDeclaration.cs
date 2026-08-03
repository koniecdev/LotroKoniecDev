using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Declares the exchanges, the queues and the bindings of <see cref="RabbitMqTopology"/>. AMQP
/// declarations are idempotent, so both the publisher and the consumer run the same declaration
/// on channel open: whichever side starts first creates the topology, the other becomes a no-op.
/// The publisher needs it because a topic exchange silently drops messages that reach no bound
/// queue; the consumer needs it because consuming from a queue that nobody declared yet fails.
/// </summary>
/// <remarks>
/// Idempotent only while the arguments stay identical — redeclaring an existing queue with
/// different arguments fails the channel with <c>PRECONDITION_FAILED</c>. Changing anything in
/// <see cref="EmailQueueArguments"/> therefore requires deleting the queue first (dev:
/// <c>docker compose down -v</c>); see ADR-0036.
/// </remarks>
public static class RabbitMqTopologyDeclaration
{
    /// <summary>
    /// <see cref="RabbitMqTopology.EmailQueue"/> is a quorum queue so the broker itself tracks
    /// redeliveries (<c>x-delivery-count</c>) and enforces
    /// <see cref="RabbitMqTopology.EmailDeliveryLimit"/> — classic queues count neither, which
    /// would push retry bookkeeping into every consumer. Exhausted and rejected messages
    /// dead-letter to <see cref="RabbitMqTopology.EmailsDeadLetterExchange"/>; the
    /// <c>at-least-once</c> strategy makes that hop as loss-proof as the publish that delivered
    /// the message (the default <c>at-most-once</c> may drop dead letters under pressure), and it
    /// requires <c>x-overflow: reject-publish</c> — inert here because no <c>x-max-length</c> is
    /// set, so nothing ever overflows.
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
    /// The parking lot is a quorum queue for the same durability as the queue it backstops; no
    /// dead-letter exchange of its own and no explicit delivery limit — it is terminal. The
    /// quorum <em>default</em> delivery limit (20) still applies though, so any replay must
    /// ack-and-republish, never reject-requeue — a reject-requeue loop would silently drop the
    /// parked message (ADR-0036).
    /// </summary>
    private static readonly Dictionary<string, object?> DeadLetterQueueArguments = new()
    {
        ["x-queue-type"] = "quorum"
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // Dead-letter side first: the parking lot must exist before anything can be routed to
        // it, or dead letters published in the gap would reach an exchange with no bound queue.
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
