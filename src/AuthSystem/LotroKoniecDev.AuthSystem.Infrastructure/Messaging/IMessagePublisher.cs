namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Hands one already-serialized message to the broker.
/// </summary>
/// <remarks>
/// Failures surface as exceptions rather than a <c>Result</c>: a refused publish is an
/// infrastructure fault (broker down, unroutable key, nack), not a business outcome, and the
/// caller — the outbox relay — needs the transport detail to decide between retrying and
/// recording the failure on the outbox row.
/// </remarks>
public interface IMessagePublisher
{
    /// <param name="routingKey">A key from <see cref="RabbitMqTopology"/>.</param>
    /// <param name="payload">The serialized message body; UTF-8 JSON.</param>
    /// <param name="messageId">
    /// The outbox row's identifier, carried on the wire as the AMQP <c>message-id</c> so the
    /// consumer can deduplicate redeliveries against it.
    /// </param>
    /// <param name="cancellationToken">The cancellation token</param>
    Task PublishAsync(
        string routingKey,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken);
}
