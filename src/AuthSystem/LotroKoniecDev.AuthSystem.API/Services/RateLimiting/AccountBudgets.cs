namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The per-account budgets, in one place so the registration and the tests read the same numbers.
/// </summary>
internal static class AccountBudgets
{
    /// <summary>
    /// The window every per-account budget uses: the same quarter of an hour as the auth pages' POST
    /// budgets, so "come back later" means the same wait everywhere.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Password-reset mails per account (#692).</summary>
    internal const int PasswordResetPermitLimit = 3;

    /// <summary>
    /// Current-password confirmations per account across the endpoints that ask for one (#813,
    /// ADR-0053): the same room the login form gives a client for wrong passwords (auth-page-limit),
    /// enough for a few typos and every sensitive action in a row, far too few for a guessing script.
    /// </summary>
    internal const int PasswordConfirmationPermitLimit = 10;
}
