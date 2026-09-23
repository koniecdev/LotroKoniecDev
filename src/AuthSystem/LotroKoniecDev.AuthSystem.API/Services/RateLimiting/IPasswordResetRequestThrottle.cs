using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for password-reset mail that belongs to the account or the inbox the mail would reach,
/// not to the caller asking for it (ADR-0057). The IP policies cannot do this: an attacker who rotates IPs
/// gets a fresh budget every time, and the victim gets every one of those mails.
/// </summary>
internal interface IPasswordResetRequestThrottle
{
    /// <summary>
    /// Takes one permit for the mail to <paramref name="user"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(ApplicationUser user);
}
