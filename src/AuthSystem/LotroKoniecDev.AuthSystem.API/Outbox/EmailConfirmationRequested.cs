namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Outbox payload contract for "send this user their confirmation e-mail", serialised by the
/// producer and deserialised by the relay and the consumer.
/// </summary>
/// <remarks>
/// Carries the user id alone, on purpose. The confirmation token is a delivery detail rather than
/// part of the intent, and it expires — minting it here would start its lifetime at registration
/// while the e-mail promises the countdown starts at delivery, making that sentence false for every
/// message that waits in the queue, gets retried, or sits in a dead-letter queue. The consumer
/// mints the token and reads the address off the user at the moment it sends.
/// </remarks>
public sealed record EmailConfirmationRequested(Guid IdentityUserId);
