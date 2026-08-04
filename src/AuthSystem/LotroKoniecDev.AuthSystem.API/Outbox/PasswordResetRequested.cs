namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Outbox payload contract for "send this user their password reset e-mail", serialised by the
/// producer and deserialised by the relay and the consumer.
/// </summary>
/// <remarks>
/// Carries the user id alone, on purpose (ADR-0038 decision 2). A live reset token must never
/// persist in an outbox row, a broker frame, or a DLQ-parked message — minting it at delivery
/// keeps it out of all three, keeps it valid against the <em>current</em> security stamp no matter
/// how long the message waited, and makes the e-mail's "link expires in …" countdown start at
/// delivery. The deletion-window guard lives at delivery too: the processor, not the writers,
/// decides whether a reset may go out while account deletion is scheduled.
/// </remarks>
public sealed record PasswordResetRequested(Guid IdentityUserId);
