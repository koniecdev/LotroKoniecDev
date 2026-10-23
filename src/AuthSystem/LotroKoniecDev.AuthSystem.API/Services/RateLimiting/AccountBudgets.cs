namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// In one place so the registration and the tests read the same numbers.
/// </summary>
internal static class AccountBudgets
{
    /// <summary>
    /// The same quarter hour as the auth pages' POST budgets, so "come back later" means the same wait
    /// everywhere.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per hour, not per quarter (#811): one per quarter would still allow four loops an hour.
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
    /// Per account across every endpoint that asks for the current password (ADR-0053): the same room as
    /// the login form, enough for typos, far too few for a guessing script.
    /// </summary>
    internal const int PasswordConfirmationPermitLimit = 10;

    /// <summary>
    /// Login attempts per account while its deletion is scheduled (#881). On average below the 15 per
    /// quarter hour Identity's lockout allows, though a fixed window lets a burst of 20 through at its edge.
    /// The owner cannot sign in then anyway, and the deletion mail carries the date and cancel link.
    /// </summary>
    internal const int DeletionScheduledLoginPermitLimit = 10;

    /// <summary>
    /// A person needs two at most: a schedule, and one more after a cancel (#811). The third covers a
    /// permit spent without a schedule, such as a failed save or the losing half of a double submit.
    /// </summary>
    internal const int DeletionSchedulePermitLimit = 3;
}
