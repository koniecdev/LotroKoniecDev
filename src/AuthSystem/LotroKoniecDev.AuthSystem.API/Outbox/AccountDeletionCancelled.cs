namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Tell this user their deletion was cancelled."
/// </summary>
/// <remarks>
/// Only the user id and no token (ADR-0038 decision 2): the e-mail only informs. The reset token is in the
/// cancel endpoint's response.
/// </remarks>
public sealed record AccountDeletionCancelled(Guid IdentityUserId);
