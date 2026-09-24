namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budget of deletion schedules an account gets (#811). The confirmation budget alone lets one account
/// run schedule, cancel and reset about forty times an hour, and every schedule mails the account's
/// address and, while an undo is armed, the address the undo would restore. That second inbox belongs to
/// somebody the caller may not control.
/// </summary>
internal interface IAccountDeletionScheduleThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/> and reports whether there was one left. It runs as
    /// the last check before the schedule is saved, so a wrong password or an already scheduled deletion
    /// never spends one (ADR-0055).
    /// </summary>
    bool TryAcquire(Guid userId);
}
