namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for resent confirmation mail that belongs to the inbox the mail would reach, not to the
/// caller asking for it (ADR-0055). The resend form is anonymous and takes any address, so the IP policy
/// alone gives an attacker who rotates IPs a fresh budget every time.
/// </summary>
/// <remarks>
/// Per inbox, not per account: every spelling a stranger registers would otherwise bring a fresh budget
/// (ADR-0057).
/// </remarks>
internal interface IEmailConfirmationResendThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="mailbox"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(MailboxKey mailbox);
}
