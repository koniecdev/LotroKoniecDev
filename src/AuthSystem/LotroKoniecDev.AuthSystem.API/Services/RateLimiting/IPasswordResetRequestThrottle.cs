namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for password-reset mail that belongs to the account the mail would reach, not to the
/// caller asking for it. The IP policies cannot do this: an attacker who rotates IPs gets a fresh budget
/// every time, and the victim gets every one of those mails.
/// </summary>
internal interface IPasswordResetRequestThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(Guid userId);
}
