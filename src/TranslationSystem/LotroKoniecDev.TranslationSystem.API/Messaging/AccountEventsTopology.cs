using LotroKoniecDev.SharedKernel.IntegrationEvents;
using RabbitMQ.Client;

namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// The TMS's part of the account events topology (ADR-0065). The AuthSystem owns and declares
/// <see cref="AccountEvents.Exchange"/>; the TMS only checks it exists, so the two can never declare it
/// with different arguments and the TMS broker user needs no right to create it. An event published
/// before this queue exists returns to the AuthSystem's outbox (<c>mandatory</c> + confirms), so it is
/// never dropped.
/// </summary>
/// <remarks>
/// A changed argument fails the channel with <c>PRECONDITION_FAILED</c>, so changing
/// <see cref="ErasedQueueArguments"/> means deleting the queue (ADR-0036). Delete it only while the
/// release with the new arguments runs: the running consumer declares it again with its own.
/// </remarks>
internal static class AccountEventsTopology
{
    public const string ErasedQueue = "tms.account-erased";

    /// <summary>
    /// Nothing consumes <see cref="ErasedDeadLetterQueue"/>: a person looks at it and replays the
    /// message to <see cref="AccountEvents.Exchange"/>.
    /// </summary>
    public const string ErasedDeadLetterExchange = "tms.account-erased.dlx";

    /// <summary>
    /// Unlike the e-mail parking lot, never give it a <c>message-ttl</c>: a parked message is erasure
    /// work and waits for a person however long that takes (ADR-0036 §6, #1085).
    /// </summary>
    public const string ErasedDeadLetterQueue = "tms.account-erased.dlq";

    /// <summary>
    /// Counts a <c>basic.reject</c> or a lost connection, not the <c>basic.nack</c> the consumer sends on
    /// a database outage (RabbitMQ 4.3), so it bounds only a message that keeps breaking the consumer.
    /// </summary>
    public const int ErasedDeliveryLimit = 5;

    /// <summary>
    /// A quorum queue, so the broker counts the retries itself, as on the e-mail queue (ADR-0036).
    /// </summary>
    private static readonly Dictionary<string, object?> ErasedQueueArguments = new()
    {
        ["x-queue-type"] = "quorum",
        ["x-dead-letter-exchange"] = ErasedDeadLetterExchange,
        ["x-delivery-limit"] = ErasedDeliveryLimit,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish"
    };

    private static readonly Dictionary<string, object?> DeadLetterQueueArguments = new()
    {
        ["x-queue-type"] = "quorum"
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        // Dead-letter side first, so nothing is dead-lettered into an exchange with no queue behind it.
        await channel.ExchangeDeclareAsync(
            exchange: ErasedDeadLetterExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: ErasedDeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: DeadLetterQueueArguments,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: ErasedDeadLetterQueue,
            exchange: ErasedDeadLetterExchange,
            routingKey: string.Empty,
            arguments: null,
            cancellationToken: cancellationToken);

        // Fails on a fresh broker until the AuthSystem declares it; the consumer keeps retrying.
        await channel.ExchangeDeclarePassiveAsync(AccountEvents.Exchange, cancellationToken);

        await channel.QueueDeclareAsync(
            queue: ErasedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: ErasedQueueArguments,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: ErasedQueue,
            exchange: AccountEvents.Exchange,
            routingKey: AccountEvents.ErasedRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
    }
}
