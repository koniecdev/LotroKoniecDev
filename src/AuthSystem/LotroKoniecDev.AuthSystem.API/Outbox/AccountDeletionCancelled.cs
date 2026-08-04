namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Outbox payload contract for "send this user the deletion-cancelled courtesy notice",
/// serialised by the producer and deserialised by the relay and the consumer.
/// </summary>
/// <remarks>
/// Carries the user id alone (ADR-0038 decision 2) — and deliberately no token: the e-mail is a
/// courtesy notice pointing at the password-reset form, while the forced-reset token itself
/// travels in the cancel endpoint's response, minted by the handler after its commit. Epic #578's
/// table overstated this e-mail's content; the ADR's code-verified inventory is authoritative.
/// </remarks>
public sealed record AccountDeletionCancelled(Guid IdentityUserId);
