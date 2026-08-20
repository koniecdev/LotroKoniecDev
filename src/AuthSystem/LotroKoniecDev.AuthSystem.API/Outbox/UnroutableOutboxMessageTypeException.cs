namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Thrown when a feature slice enqueues an outbox message whose type has no routing key in
/// <see cref="OutboxMessageRouting"/>. That is a programmer error, a forgotten routing entry, and this
/// shows it at write time instead of blocking the relay after the commit.
/// It has its own type on purpose. Writers catch broad exception types for safety, for example
/// <c>RegisterUser</c> catches <see cref="InvalidOperationException"/> for an Identity lookup race,
/// and this failure must crash loudly instead of looking like a business outcome.
/// </summary>
internal sealed class UnroutableOutboxMessageTypeException : Exception
{
    public UnroutableOutboxMessageTypeException(string type)
        : base($"Outbox message type '{type}' has no routing key mapped in {nameof(OutboxMessageRouting)}.")
    {
    }
}
