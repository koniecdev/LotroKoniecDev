namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Send this user their confirmation e-mail."
/// </summary>
/// <remarks>
/// Only the user id: the e-mail says the token's countdown starts when the user receives it, which would
/// be false for a token created before a message waited, was retried or was dead-lettered.
/// </remarks>
public sealed record EmailConfirmationRequested(Guid IdentityUserId);
