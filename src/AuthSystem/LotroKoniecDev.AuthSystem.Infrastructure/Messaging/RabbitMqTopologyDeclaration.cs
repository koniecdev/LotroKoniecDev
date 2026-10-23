using LotroKoniecDev.SharedKernel.IntegrationEvents;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Both the publisher and the consumer run this when they open a channel, and whichever starts first
/// creates the topology. The publisher needs it because a topic exchange silently drops a message that
/// reaches no bound queue. The consumer needs it because consuming from an undeclared queue fails.
/// </summary>
/// <remarks>
/// Declaring an existing queue with different arguments fails the channel with
/// <c>PRECONDITION_FAILED</c>. So changing <see cref="EmailQueueArguments"/> means deleting the queue:
/// in dev with <c>docker compose down -v</c>, on a live box only while the release with the new
/// arguments runs, so its consumer declares the queue again (#1088, ADR-0036, the runbook).
/// </remarks>
public static class RabbitMqTopologyDeclaration
{
    /// <summary>
    /// A quorum queue, so the broker counts retries in <c>x-delivery-count</c> and applies
    /// <see cref="RabbitMqTopology.EmailDeliveryLimit"/>; with a classic queue the consumer would have to.
    /// <c>at-least-once</c> dead-lettering, because the default <c>at-most-once</c> can drop dead letters
    /// under load. That strategy requires <c>x-overflow: reject-publish</c>, which does nothing here
    /// because no <c>x-max-length</c> is set.
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
    /// Quorum too, so it is as durable as the queue it backs up, and with no dead-letter exchange because
    /// it is the end of the line. The quorum default delivery limit of 20 still applies, so a replay must
    /// ack and publish again: rejecting and requeueing in a loop would lose the message (ADR-0036).
    /// The 7-day expiry is a deploy-time broker policy, because an argument added here could not reach
    /// the queue that already exists on a live box (ADR-0036 §6).
    /// </summary>
    private static readonly Dictionary<string, object?> DeadLetterQueueArguments = new()
    {
        ["x-queue-type"] = "quorum"
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // The dead-letter side first, or a dead letter in the gap would reach an exchange with no queue.
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

        // Declared so a publish never fails on a missing exchange. Its queues belong to the consuming
        // contexts (ADR-0065); until one is bound, a mandatory publish comes back unroutable and the
        // outbox row waits for the next try.
        await channel.ExchangeDeclareAsync(
            exchange: AccountEvents.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
    }
}
