namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// A forgotten entry in <see cref="OutboxMessageRouting"/>, shown at write time instead of in the relay
/// after the commit. Its own type, so no writer catches it and it can never become a business outcome.
/// </summary>
internal sealed class UnroutableOutboxMessageTypeException : Exception
{
    public UnroutableOutboxMessageTypeException(string type)
        : base($"Outbox message type '{type}' has no routing key mapped in {nameof(OutboxMessageRouting)}.")
    {
    }
}
