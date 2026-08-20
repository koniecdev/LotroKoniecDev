namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// The one place the broker topology is defined. The publisher sends to <see cref="EmailsExchange"/>
/// with one of the routing keys below, and the consumer declares <see cref="EmailQueue"/> bound to
/// the same exchange with <see cref="EmailBindingPattern"/>. Both sides use these constants, so a typo
/// can no longer put a routing key and a binding out of step.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>
    /// The topic exchange every outgoing e-mail goes through. It is a topic and not a direct exchange
    /// even though one queue consumes it today, so a second consumer, for metrics, a digest or another
    /// transport, can bind its own subset of the keys later without touching the publisher.
    /// </summary>
    public const string EmailsExchange = "lotro.emails";

    /// <summary>
    /// The queue the e-mail sender consumes.
    /// </summary>
    public const string EmailQueue = "emails.send";

    /// <summary>
    /// The binding pattern for <see cref="EmailQueue"/>. It uses <c>#</c>, which matches any number of
    /// words, and not <c>*</c>, which matches exactly one. So a key that gains a segment, such as
    /// <c>email.password-reset.retry</c>, still routes without a binding change.
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
    /// The dead-letter exchange. The broker sends here every message <see cref="EmailQueue"/> gives up
    /// on, either because the consumer rejected it or because it passed
    /// <see cref="EmailDeliveryLimit"/>. It is a fanout, because everything that fails goes to the same
    /// place and there is no routing to decide. The original routing key stays on the message, so it
    /// can be replayed later.
    /// </summary>
    public const string EmailsDeadLetterExchange = "lotro.emails.dlx";

    /// <summary>
    /// The queue bound to <see cref="EmailsDeadLetterExchange"/> where failed messages wait. Nothing
    /// consumes it: a person looks at them in the management UI and replays them to
    /// <see cref="EmailsExchange"/> under their original routing key.
    /// </summary>
    public const string EmailDeadLetterQueue = "emails.send.dlq";

    /// <summary>
    /// How often the broker delivers a message of <see cref="EmailQueue"/> again before it gives up
    /// (<c>x-delivery-limit</c>): one first delivery plus this many retries, then the dead-letter
    /// queue. The broker enforces it, so the limit holds across consumer restarts, and a crash and
    /// requeue counts like any other retry. Change it together with the consumer's backoff ladder,
    /// which has one entry per allowed retry.
    /// </summary>
    public const int EmailDeliveryLimit = 5;
}
