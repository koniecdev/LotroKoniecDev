namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Hands one already-serialized message to the broker.
/// </summary>
/// <remarks>
/// Failures come out as exceptions and not as a <c>Result</c>. A refused publish is an
/// infrastructure fault, such as the broker being down, a key nothing is bound to, or a nack. It is
/// not a business outcome, and the caller, the outbox relay, needs the transport detail to choose
/// between retrying and recording the failure on the outbox row.
/// </remarks>
public interface IMessagePublisher
{
    /// <param name="routingKey">A key from <see cref="RabbitMqTopology"/>.</param>
    /// <param name="type">
    /// The name of the outbox row's payload contract. It travels as the AMQP <c>type</c> property, so
    /// the consumer can pick the processor that owns the payload (ADR-0038). It is kept apart from
    /// <paramref name="routingKey"/> on purpose: the routing key only decides which bindings receive
    /// the message.
    /// </param>
    /// <param name="payload">The serialized message body, as UTF-8 JSON.</param>
    /// <param name="messageId">
    /// The outbox row's id. It travels as the AMQP <c>message-id</c>, so the consumer can recognise a
    /// message it has already handled.
    /// </param>
    Task PublishAsync(
        string routingKey,
        string type,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken);
}
