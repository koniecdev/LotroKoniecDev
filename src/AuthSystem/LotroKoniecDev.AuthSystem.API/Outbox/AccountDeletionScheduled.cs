namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Outbox payload contract for "send this user their deletion-scheduled e-mail with the cancel
/// link", serialised by the producer and deserialised by the relay and the consumer.
/// </summary>
/// <remarks>
/// Carries the user id alone, on purpose (ADR-0038 decision 2). The cancel token is minted at
/// delivery — a live token must never persist in an outbox row, a broker frame, or a DLQ-parked
/// message, and minting late keeps it bound to the <em>current</em> security stamp (final before
/// the row became visible: the writer rotates the stamp in the same save that commits this row).
/// The e-mailed deletion date is derivable state, so it is recomputed at delivery too:
/// <c>DeletionScheduledAt + GdprSettings.DeletionGracePeriod</c>, the finalizer's own formula —
/// snapshotting it here could drift from what the finalizer will actually do.
/// </remarks>
public sealed record AccountDeletionScheduled(Guid IdentityUserId);
