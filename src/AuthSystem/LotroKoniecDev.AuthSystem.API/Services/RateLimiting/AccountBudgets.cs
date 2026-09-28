namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budgets the handlers and pages take themselves, in one place so the registration and the tests read
/// the same numbers.
/// </summary>
internal static class AccountBudgets
{
    /// <summary>
    /// The window every in-handler and in-page budget uses except the deletion schedule: the same quarter of
    /// an hour as the auth pages' POST budgets, so "come back later" means the same wait everywhere.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The deletion schedule counts per hour, not per quarter (#811). One schedule per quarter would still
    /// allow four loops an hour, and nobody needs to schedule a deletion more than twice in an hour.
    /// </summary>
    internal static readonly TimeSpan DeletionScheduleWindow = TimeSpan.FromHours(1);

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

    /// <summary>
    /// Login attempts per account while its deletion is scheduled (#881). The same 10 per quarter hour as the
    /// confirmation budget and the login page's limit per address. On average that is below the 15 per
    /// quarter hour Identity's lockout allows on a normal account, though a fixed window lets a burst of 20
    /// through at its edge. The owner cannot sign in during the grace period anyway: a spent budget shows
    /// them the general message instead of the deletion date, and the deletion mail still carries the date
    /// and the cancel link.
    /// </summary>
    internal const int DeletionScheduledLoginPermitLimit = 10;

    /// <summary>
    /// Deletion schedules per account per <see cref="DeletionScheduleWindow"/> (#811). A person needs two at
    /// most: a schedule, and one more after a cancel. The third is room for a permit spent without a
    /// schedule, because a fixed window never gives one back: a save that failed, or the losing half of a
    /// double-submitted form, which passes every check before the winner's save lands.
    /// </summary>
    internal const int DeletionSchedulePermitLimit = 3;
}
