namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The outbox payload for "this account has moved to a new address". It tells the new mailbox the
/// change is done and gives the old one the link that undoes it (ADR-0048).
/// </summary>
/// <remarks>
/// <see cref="PreviousEmail"/> is only in this payload. By the time the processor runs, the user row
/// no longer holds it, and it is both the address the notice goes to and half of the revert token's
/// purpose.
/// </remarks>
public sealed record EmailChangeCompleted(Guid IdentityUserId, string PreviousEmail, string NewEmail);
