namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// "Send this user their password reset e-mail."
/// </summary>
/// <remarks>
/// Only the user id (ADR-0038 decision 2): a working token must never sit in an outbox row, a broker frame
/// or a dead letter. Created at send time, it matches the current stamp and its countdown starts on
/// arrival. The scheduled-deletion check also happens at send time.
/// </remarks>
public sealed record PasswordResetRequested(Guid IdentityUserId);
