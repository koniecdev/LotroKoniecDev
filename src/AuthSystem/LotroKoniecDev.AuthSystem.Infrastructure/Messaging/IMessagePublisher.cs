namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <remarks>
/// Failures come out as exceptions, not as a <c>Result</c>: a refused publish is an infrastructure
/// fault, and the outbox relay needs the transport detail to choose between retrying and recording the
/// failure. A key nothing is bound to comes out as <see cref="MessageNotRoutedException"/>, because it
/// concerns that one message, not the broker.
/// </remarks>
public interface IMessagePublisher
{
    /// <param name="exchange">
    /// <see cref="RabbitMqTopology.EmailsExchange"/> for e-mail work, or the account events exchange the
    /// TranslationSystem binds to (ADR-0065).
    /// </param>
    /// <param name="routingKey">Decides only which bindings receive the message.</param>
    /// <param name="type">
    /// The payload contract name, sent as the AMQP <c>type</c> property. The consumer picks its processor
    /// by it (ADR-0038).
    /// </param>
    /// <param name="payload">The serialized body, as UTF-8 JSON.</param>
    /// <param name="messageId">
    /// The outbox row's id, sent as the AMQP <c>message-id</c>, so the consumer can recognise a message it
    /// has already handled.
    /// </param>
    Task PublishAsync(
        string exchange,
        string routingKey,
        string type,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken);
}
