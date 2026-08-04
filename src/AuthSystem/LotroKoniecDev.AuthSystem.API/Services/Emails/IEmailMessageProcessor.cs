using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The per-type seam of the e-mail dispatch pipeline (ADR-0038): one implementation per outbox
/// message type, owning that type's payload deserialization and its business reaction (loading
/// state, minting tokens at send time, dispatching the e-mail). The consumer selects the
/// implementation from the keyed-service registry in the API's dependency injection — an
/// explicit, compile-visible inventory keyed by the outbox row's <c>Type</c> (ADR-0001: no
/// assembly scanning), which travels on the wire as the AMQP <c>type</c> property.
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so every implementation must stay idempotent — a
/// redelivered message must never do harm, only at worst repeat a harmless send. A new message
/// type re-earns that property before it may join the registry.
/// </remarks>
internal interface IEmailMessageProcessor
{
    /// <summary>
    /// Deserializes and validates one delivery's payload. <c>null</c> is the poison verdict:
    /// the body cannot be read as this processor's contract (or fails its invariants), no
    /// redelivery can fix it, and the consumer parks it in the dead-letter queue.
    /// </summary>
    object? TryDeserialize(ReadOnlySpan<byte> body);

    /// <summary>
    /// Handles one deserialized message end-to-end and returns the acknowledgement decision.
    /// <paramref name="message"/> is the exact object a prior <see cref="TryDeserialize"/> call
    /// returned — passing anything else is a programmer error.
    /// </summary>
    /// <returns>
    /// NOT a business outcome — the answer to "does this message need redelivery?". Success
    /// means "ack, drop it from the queue"; failure means "worth retrying" and drives the
    /// consumer's reject + requeue ladder.
    /// </returns>
    Task<Result> ProcessAsync(object message, CancellationToken cancellationToken);
}
