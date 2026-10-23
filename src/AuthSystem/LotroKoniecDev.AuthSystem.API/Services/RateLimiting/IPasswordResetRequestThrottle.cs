using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Belongs to the account or inbox the mail reaches, not the caller (ADR-0057): an attacker who rotates
/// IPs gets a fresh IP budget every time, and the victim gets every mail.
/// </summary>
internal interface IPasswordResetRequestThrottle
{
    bool TryAcquire(ApplicationUser user);
}
