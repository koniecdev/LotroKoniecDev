namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "This account has moved": a notice to the new mailbox, and the undo link to the old one (ADR-0048).
/// </summary>
/// <remarks>
/// <see cref="PreviousEmail"/> lives only here: the row no longer holds it, and it is both the notice's
/// recipient and half of the revert token's purpose.
/// </remarks>
public sealed record EmailChangeCompleted(Guid IdentityUserId, string PreviousEmail, string NewEmail);
