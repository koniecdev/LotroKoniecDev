namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth;

/// <summary>
/// A request that carries the caller's current password for the auth API to confirm. Each one spends a
/// permit of the account's confirmation budget (ADR-0053), so a client sends it exactly once: a retried
/// request is a second confirmation, not the same one.
/// </summary>
public interface IPasswordConfirmationRequest
{
}
