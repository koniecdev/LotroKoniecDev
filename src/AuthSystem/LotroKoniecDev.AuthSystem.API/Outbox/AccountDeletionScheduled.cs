namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "send this user the deletion-scheduled e-mail with the cancel link". The
/// producer serializes it, and the relay and the consumer read it back.
/// </summary>
/// <remarks>
/// It carries the user id and nothing else, on purpose (ADR-0038 decision 2). The cancel token is
/// created when the e-mail is sent. A working token must never sit in an outbox row, in a broker
/// frame or in a dead-lettered message, and creating it late also ties it to the current security
/// stamp. That stamp is already final when the row becomes visible, because the writer changes it in
/// the same save that commits this row.
/// The deletion date in the e-mail can be computed, so it is computed at send time as well:
/// <c>DeletionScheduledAt + GdprSettings.DeletionGracePeriod</c>, the same formula the finalizer
/// uses. Storing it here could drift from what the finalizer really does.
/// </remarks>
public sealed record AccountDeletionScheduled(Guid IdentityUserId);
