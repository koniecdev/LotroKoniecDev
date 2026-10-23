namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// The one place the broker topology is defined. The publisher and the consumer both use these
/// constants, so a routing key and a binding cannot drift apart.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>
    /// A topic exchange, not a direct one, so a second consumer can later bind its own subset of the
    /// keys without touching the publisher.
    /// </summary>
    public const string EmailsExchange = "lotro.emails";

    public const string EmailQueue = "emails.send";

    /// <summary>
    /// <c>#</c> matches any number of words and <c>*</c> exactly one, so a key that gains a segment,
    /// such as <c>email.password-reset.retry</c>, still routes without a binding change.
    /// </summary>
    public const string EmailBindingPattern = "email.#";

    public const string EmailConfirmationRoutingKey = "email.confirmation";

    /// <summary>
    /// The notice to an account's owner that someone tried to register with its address.
    /// </summary>
    public const string RegistrationAttemptedRoutingKey = "email.registration-attempted";

    public const string PasswordResetRoutingKey = "email.password-reset";

    public const string DeletionScheduledRoutingKey = "email.deletion-scheduled";

    public const string DeletionCancelledRoutingKey = "email.deletion-cancelled";

    /// <summary>
    /// Carries both the verification link for the new address and the warning for the old one.
    /// </summary>
    public const string EmailChangeRequestedRoutingKey = "email.change-requested";

    /// <summary>
    /// The change notice, which also carries the revert link to the old address.
    /// </summary>
    public const string EmailChangeCompletedRoutingKey = "email.change-completed";

    /// <summary>
    /// Receives every message <see cref="EmailQueue"/> rejects or that passes
    /// <see cref="EmailDeliveryLimit"/>. A fanout, because all failures go to one place. The original
    /// routing key stays on the message, so it can be replayed.
    /// </summary>
    public const string EmailsDeadLetterExchange = "lotro.emails.dlx";

    /// <summary>
    /// Nothing consumes it: a person replays parked messages from the management UI. A message expires
    /// 7 days after it parks, because an e-mail change message holds two addresses an account erasure
    /// cannot reach. That expiry is a broker policy in <c>scripts/hetzner/deploy.sh</c> whose pattern
    /// matches this name, so renaming the queue means changing the policy too (ADR-0036 §6).
    /// </summary>
    public const string EmailDeadLetterQueue = "emails.send.dlq";

    /// <summary>
    /// The broker's <c>x-delivery-limit</c>: one first delivery plus this many retries, then the
    /// dead-letter queue. The broker counts, so the limit holds across consumer restarts. Change it
    /// together with the consumer's backoff ladder, which has one entry per retry.
    /// </summary>
    public const int EmailDeliveryLimit = 5;
}
