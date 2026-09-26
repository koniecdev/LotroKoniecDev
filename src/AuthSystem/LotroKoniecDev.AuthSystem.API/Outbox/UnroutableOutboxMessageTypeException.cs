namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Thrown when a feature slice enqueues an outbox message whose type has no routing key in
/// <see cref="OutboxMessageRouting"/>. That is a programmer error, a forgotten routing entry, and this
/// shows it at write time instead of blocking the relay after the commit.
/// It has its own type on purpose: no writer catches it, so this failure crashes loudly and can never
/// be turned into a business outcome.
/// </summary>
internal sealed class UnroutableOutboxMessageTypeException : Exception
{
    public UnroutableOutboxMessageTypeException(string type)
        : base($"Outbox message type '{type}' has no routing key mapped in {nameof(OutboxMessageRouting)}.")
    {
    }
}
