using LotroKoniecDev.SharedKernel.IntegrationEvents;
using RabbitMQ.Client;

namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// The TMS's own part of the account events topology (ADR-0065). The AuthSystem owns
/// <see cref="AccountEvents.Exchange"/> and declares it. The TMS only checks that it exists, so the two
/// contexts can never declare it with different arguments, and the TMS's broker user needs no right to
/// create or delete it. The queue, its dead-letter side and the binding belong to the TMS alone, so the
/// AuthSystem never has to know them.
/// The AuthSystem publishes with <c>mandatory</c> and waits for the broker's confirmation, so an
/// event published before this queue exists comes back to its outbox and is sent again later. It is
/// never dropped.
/// </summary>
/// <remarks>
/// Declaring the same thing twice is safe only while the arguments stay the same. A changed argument
/// fails the channel with <c>PRECONDITION_FAILED</c>, so changing <see cref="ErasedQueueArguments"/>
/// means deleting the queue first, as with the e-mail queue (ADR-0036).
/// </remarks>
internal static class AccountEventsTopology
{
    public const string ErasedQueue = "tms.account-erased";

    /// <summary>
    /// Where the broker sends a message the consumer rejected or that passed
    /// <see cref="ErasedDeliveryLimit"/>. Nothing consumes <see cref="ErasedDeadLetterQueue"/>: a person
    /// looks at it and replays the message to <see cref="AccountEvents.Exchange"/>.
    /// </summary>
    public const string ErasedDeadLetterExchange = "tms.account-erased.dlx";

    public const string ErasedDeadLetterQueue = "tms.account-erased.dlq";

    /// <summary>
    /// How often the broker hands a message out again after a <c>basic.reject</c> or a lost connection
    /// before it parks it. A database failure does not count against it: the consumer returns the
    /// message with <c>basic.nack</c>, which RabbitMQ 4.3 does not count, so an outage of any length
    /// loses no erasure. What it does bound is a message that keeps breaking the consumer itself.
    /// </summary>
    public const int ErasedDeliveryLimit = 5;

    /// <summary>
    /// A quorum queue, so the broker counts the retries and applies the limit itself, the same shape
    /// as the AuthSystem's e-mail queue (ADR-0036).
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
        // The dead-letter side comes first, so nothing can be dead-lettered into an exchange with no
        // queue behind it.
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

        // Fails while the AuthSystem has not declared it yet, on a fresh broker. The consumer then
        // retries its connection until it is there.
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
