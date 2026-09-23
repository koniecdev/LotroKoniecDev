namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budgets the handlers take themselves, in one place so the registration and the tests read the same
/// numbers.
/// </summary>
internal static class AccountBudgets
{
    /// <summary>
    /// The window every in-handler budget uses: the same quarter of an hour as the auth pages' POST
    /// budgets, so "come back later" means the same wait everywhere.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Password-reset mails per confirmed account, and per inbox across unconfirmed accounts (#692, #835).
    /// </summary>
    internal const int PasswordResetPermitLimit = 3;

    /// <summary>Resent confirmation mails per inbox (#793, #835).</summary>
    internal const int EmailConfirmationResendPermitLimit = 3;

    /// <summary>E-mail change links per inbox of the new address, whichever accounts ask for them (#793, #835).</summary>
    internal const int EmailChangeRecipientPermitLimit = 3;

    /// <summary>New accounts, and so confirmation mails, per inbox (#835).</summary>
    internal const int RegistrationPermitLimit = 3;

    /// <summary>
    /// Current-password confirmations per account across the endpoints that ask for one (#813,
    /// ADR-0053): the same room the login form gives a client for wrong passwords (auth-page-limit),
    /// enough for a few typos and every sensitive action in a row, far too few for a guessing script.
    /// </summary>
    internal const int PasswordConfirmationPermitLimit = 10;
}
