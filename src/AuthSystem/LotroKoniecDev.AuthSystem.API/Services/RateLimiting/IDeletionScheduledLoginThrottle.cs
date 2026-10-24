namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Login attempts for an account with a scheduled deletion (#881, ADR-0053 amendment). It is locked for
/// the grace period, so Identity's lockout never slows guessing, and the page limit resets for an
/// attacker who changes address.
/// </summary>
/// <remarks>
/// Not the confirmation budget: the login page is anonymous, so a stranger would use up the budget the
/// owner needs for the data export.
/// </remarks>
internal interface IDeletionScheduledLoginThrottle
{
    /// <summary>
    /// Runs before the password check, so every attempt spends a permit, the right password included.
    /// </summary>
    bool TryAcquire(Guid userId);
}
