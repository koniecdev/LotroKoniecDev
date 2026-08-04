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

    /// <summary>
    /// Routing key of the password reset e-mail.
    /// </summary>
    public const string PasswordResetRoutingKey = "email.password-reset";

    /// <summary>
    /// Routing key of the deletion-scheduled e-mail (the cancel link).
    /// </summary>
    public const string DeletionScheduledRoutingKey = "email.deletion-scheduled";

    /// <summary>
    /// Routing key of the deletion-cancelled courtesy e-mail.
    /// </summary>
    public const string DeletionCancelledRoutingKey = "email.deletion-cancelled";

    /// <summary>
    /// Dead-letter exchange: the broker republishes here every message that
    /// <see cref="EmailQueue"/> gives up on — rejected as poison by the consumer, or past
    /// <see cref="EmailDeliveryLimit"/>. Fanout, because "everything that dies goes to the one
    /// parking lot" needs no routing decisions, and the original routing key survives on the
    /// message itself for replay.
    /// </summary>
    public const string EmailsDeadLetterExchange = "lotro.emails.dlx";

    /// <summary>
    /// The parking lot bound to <see cref="EmailsDeadLetterExchange"/>. Nothing consumes it:
    /// messages wait for a human (management UI) to diagnose and replay them back to
    /// <see cref="EmailsExchange"/> under their original routing key.
    /// </summary>
    public const string EmailDeadLetterQueue = "emails.send.dlq";

    /// <summary>
    /// How many times the broker redelivers a message of <see cref="EmailQueue"/> before
    /// dead-lettering it (<c>x-delivery-limit</c>): 1 initial delivery + this many redeliveries,
    /// then the parking lot. Broker-enforced, so the cap holds even across consumer restarts —
    /// a crash-requeue loop counts like any other redelivery. Moves together with the consumer's
    /// redelivery backoff ladder (one ladder entry per allowed redelivery).
    /// </summary>
    public const int EmailDeliveryLimit = 5;
}
