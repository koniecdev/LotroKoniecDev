namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Deletion schedules per account (#811). The confirmation budget alone allows about forty schedule,
/// cancel and reset loops an hour, and each schedule may also mail the armed undo address, an inbox the
/// caller may not control.
/// </summary>
internal interface IAccountDeletionScheduleThrottle
{
    /// <summary>
    /// The last check before the schedule is saved, so a wrong password or an existing schedule never
    /// spends a permit (ADR-0055).
    /// </summary>
    bool TryAcquire(Guid userId);
}
