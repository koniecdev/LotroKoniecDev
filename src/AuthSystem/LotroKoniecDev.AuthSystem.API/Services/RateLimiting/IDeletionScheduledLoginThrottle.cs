namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The budget of login attempts an account with a scheduled deletion gets (#881, ADR-0053 amendment). Such
/// an account is locked for its whole grace period, so Identity's lockout never slows guessing on it, and
/// the page limit per address resets for an attacker who changes address.
/// </summary>
/// <remarks>
/// It is its own budget, not the password confirmation one: the login page is anonymous, so a stranger
/// would otherwise use up the budget the owner needs for the data export during the grace period.
/// </remarks>
internal interface IDeletionScheduledLoginThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/> and reports whether there was one left. It runs before
    /// the password check, so every attempt spends one, the right password included.
    /// </summary>
    bool TryAcquire(Guid userId);
}
