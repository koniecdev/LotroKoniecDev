namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

/// <summary>
/// Cancelling a deletion also invalidates the current password, because someone else may have
/// started that deletion. So the response carries a fresh reset token and sends the caller straight
/// into the password reset flow.
/// </summary>
public sealed record CancelAccountDeletionResponse(
    string PasswordResetToken);
