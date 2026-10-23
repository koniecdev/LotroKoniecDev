namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Current-password confirmations per account, shared by every endpoint that asks for one (ADR-0053).
/// It belongs to the account, not the caller's address, so no change of address dodges it.
/// </summary>
internal interface IPasswordConfirmationThrottle
{
    /// <summary>
    /// <paramref name="userId"/> is the id the token names. Runs before the account is loaded, so a
    /// refusal costs no database read and every attempt spends a permit.
    /// </summary>
    bool TryAcquire(Guid userId);
}
