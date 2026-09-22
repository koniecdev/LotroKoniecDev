namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budget of current-password confirmations an account gets, shared by every endpoint that asks for
/// one (ADR-0053). It belongs to the account, not to the caller's address: every one of those calls
/// arrives from the frontend, so an IP policy sees one address for all users.
/// </summary>
internal interface IPasswordConfirmationThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/>, the id the token names, and reports whether there
    /// was one left. It runs after validation and before the account is loaded, so a refused request
    /// costs no database read, and every attempt spends one: the permit cannot depend on a check that has
    /// not run yet.
    /// </summary>
    bool TryAcquire(Guid userId);
}
