namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth;

/// <summary>
/// A request that carries the caller's current password. Each one spends a permit of the account's
/// confirmation budget (ADR-0053), so a client sends it once: a retry spends a second permit.
/// </summary>
public interface IPasswordConfirmationRequest
{
}
