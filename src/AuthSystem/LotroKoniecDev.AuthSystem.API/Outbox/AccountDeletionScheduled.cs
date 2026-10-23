namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Send the deletion-scheduled e-mail with the cancel link."
/// </summary>
/// <remarks>
/// Only the user id (ADR-0038 decision 2): a working token must never sit in an outbox row, a broker frame
/// or a dead letter, so the cancel token is created at send time, against a stamp the writer already
/// made final in the same save. The date is computed at send time too, through
/// <c>IAccountDeletionSchedule</c>, so it cannot drift from what the finalizer does.
/// </remarks>
public sealed record AccountDeletionScheduled(Guid IdentityUserId);
