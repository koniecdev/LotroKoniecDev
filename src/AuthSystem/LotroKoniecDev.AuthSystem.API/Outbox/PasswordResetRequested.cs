namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "send this user their password reset e-mail". The producer serializes it,
/// and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// It carries the user id and nothing else, on purpose (ADR-0038 decision 2). A working reset token
/// must never sit in an outbox row, in a broker frame or in a dead-lettered message. Creating it when
/// the e-mail is sent keeps it out of all three, keeps it valid against the current security stamp
/// however long the message waited, and makes the "link expires in …" countdown start when the user
/// receives it.
/// The check for a scheduled deletion happens at send time too: the processor, not the writer, decides
/// whether a reset may go out while an account deletion is scheduled.
/// </remarks>
public sealed record PasswordResetRequested(Guid IdentityUserId);
