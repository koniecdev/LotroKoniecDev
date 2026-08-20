namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "send this user their confirmation e-mail". The producer serializes it, and
/// the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// It carries the user id and nothing else, on purpose. The confirmation token belongs to the sending
/// step and not to the intent, and it expires. Creating it here would start its life at registration,
/// while the e-mail tells the user the countdown starts when they receive it. That sentence would be
/// wrong for every message that waits in the queue, is retried, or lands in the dead-letter queue.
/// The consumer creates the token and reads the address off the user at the moment it sends.
/// </remarks>
public sealed record EmailConfirmationRequested(Guid IdentityUserId);
