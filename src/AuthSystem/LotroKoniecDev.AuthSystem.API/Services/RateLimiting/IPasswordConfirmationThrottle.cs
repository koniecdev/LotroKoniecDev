namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budget of current-password confirmations an account gets, shared by every endpoint that asks for
/// one (ADR-0053). It belongs to the account, not to the caller's address: every one of those calls
/// arrives from the frontend, so an IP policy sees one address for all users.
/// </summary>
internal interface IPasswordConfirmationThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/> and reports whether there was one left. It runs
    /// before the password is checked, so the answer decides whether the check runs at all.
    /// </summary>
    bool TryAcquire(Guid userId);
}
