using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The per-type step of the e-mail pipeline (ADR-0038): one implementation per outbox message type,
/// which reads that type's payload and does the work, such as loading state, creating tokens at send
/// time and sending the e-mail.
/// The consumer picks the implementation from the keyed services registered in the API's dependency
/// injection. That list is written out in code and visible to the compiler, keyed by the outbox row's
/// <c>Type</c> (ADR-0001: nothing scans assemblies), which travels on the wire as the AMQP <c>type</c>
/// property.
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so every implementation has to be safe to run
/// twice. A repeat must never do harm, at worst it repeats a harmless send. A new message type has to
/// show the same property before it may join the registry.
/// </remarks>
internal interface IEmailMessageProcessor
{
    /// <summary>
    /// Reads and checks one delivery's payload. <c>null</c> means we can never handle it: the body
    /// does not match this processor's contract, or it breaks its rules, and sending it again would
    /// not help. The consumer then moves it to the dead-letter queue.
    /// </summary>
    object? TryDeserialize(ReadOnlySpan<byte> body);

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// <paramref name="message"/> must be the exact object an earlier <see cref="TryDeserialize"/> call
    /// returned. Passing anything else is a programmer error.
    /// </summary>
    /// <returns>
    /// This is not a business result. It answers one question: does this message need to be sent
    /// again? Success means "acknowledge it and drop it from the queue". Failure means "worth another
    /// try", and the consumer then rejects and requeues it with a growing pause.
    /// </returns>
    Task<Result> ProcessAsync(object message, CancellationToken cancellationToken);
}
