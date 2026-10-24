using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// One implementation per outbox message type (ADR-0038), keyed by the row's <c>Type</c>, which travels
/// as the AMQP <c>type</c> property.
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so every implementation must be safe to run twice:
/// at worst it repeats a harmless send. A new message type must show the same before it joins the
/// registry.
/// </remarks>
internal interface IEmailMessageProcessor
{
    /// <summary>
    /// <c>null</c> means the body can never be handled, so the consumer dead-letters it.
    /// </summary>
    object? TryDeserialize(ReadOnlySpan<byte> body);

    /// <summary>
    /// <paramref name="message"/> must be the object an earlier <see cref="TryDeserialize"/> returned.
    /// </summary>
    /// <returns>
    /// Not a business result. Success means "acknowledge it", also when sending again could never change
    /// anything. Failure means "worth another try", and the consumer requeues it with a growing pause.
    /// </returns>
    Task<Result> ProcessAsync(object message, CancellationToken cancellationToken);
}
