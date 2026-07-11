namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

/// <summary>
/// Cancelling a (possibly attacker-initiated) deletion invalidates the current password,
/// so the response carries a fresh reset token that sends the caller straight into the
/// forced password-reset flow.
/// </summary>
public sealed record CancelAccountDeletionResponse(
    string PasswordResetToken);
