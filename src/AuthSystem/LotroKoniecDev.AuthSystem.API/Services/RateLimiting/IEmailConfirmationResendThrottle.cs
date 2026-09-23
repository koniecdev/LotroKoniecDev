namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for resent confirmation mail that belongs to the account the mail would reach, not to
/// the caller asking for it (ADR-0055). The resend form is anonymous and takes any address, so the IP
/// policy alone gives an attacker who rotates IPs a fresh budget every time.
/// </summary>
internal interface IEmailConfirmationResendThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="userId"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(Guid userId);
}
