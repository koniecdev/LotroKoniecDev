namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Thrown when a feature slice enqueues an outbox message whose contract type has no routing key
/// in <see cref="OutboxMessageRouting"/> — a programmer error (a forgotten routing entry)
/// surfaced at write time instead of jamming the relay after commit. A dedicated type on
/// purpose: writers defensively filter broad exception families (<c>RegisterUser</c> catches
/// <see cref="InvalidOperationException"/> for an Identity lookup race), and this failure must
/// crash loudly rather than be mistaken for a business outcome.
/// </summary>
internal sealed class UnroutableOutboxMessageTypeException : Exception
{
    public UnroutableOutboxMessageTypeException(string type)
        : base($"Outbox message type '{type}' has no routing key mapped in {nameof(OutboxMessageRouting)}.")
    {
    }
}
