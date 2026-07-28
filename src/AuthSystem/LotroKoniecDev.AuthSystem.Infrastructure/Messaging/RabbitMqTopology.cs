namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// The single source of truth for the broker topology: the publisher sends to
/// <see cref="EmailsExchange"/> with one of the routing keys below, and the consumer declares
/// <see cref="EmailQueue"/> bound to the very same exchange with <see cref="EmailBindingPattern"/>.
/// Both sides share these symbols, so a routing key can no longer drift from a binding by a typo.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>
    /// Topic exchange carrying every outgoing e-mail message.
    /// Topic rather than direct even though a single queue consumes it today: a second consumer
    /// (metrics, a digest, a different transport) can bind its own subset of the keys later
    /// without touching the publisher.
    /// </summary>
    public const string EmailsExchange = "lotro.emails";

    /// <summary>
    /// The queue the e-mail sender consumes.
    /// </summary>
    public const string EmailQueue = "emails.send";

    /// <summary>
    /// Binding pattern for <see cref="EmailQueue"/>.
    /// <c>#</c> (any number of words) rather than <c>*</c> (exactly one), so keys that grow a
    /// segment — <c>email.password-reset.retry</c> — keep routing without a binding change.
    /// </summary>
    public const string EmailBindingPattern = "email.#";

    /// <summary>
    /// Routing key of the account confirmation e-mail.
    /// </summary>
    public const string EmailConfirmationRoutingKey = "email.confirmation";
}
