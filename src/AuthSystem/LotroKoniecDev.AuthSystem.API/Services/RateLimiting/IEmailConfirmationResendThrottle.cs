namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for resent confirmation mail that belongs to the inbox the mail would reach, not to the
/// caller asking for it (ADR-0055). The resend form is anonymous and takes any address, so the IP policy
/// alone gives an attacker who rotates IPs a fresh budget every time.
/// </summary>
/// <remarks>
/// Only unconfirmed accounts get a resend, and every account an attacker creates at someone else's inbox
/// stays unconfirmed. So the budget belongs to the inbox, not to one account: otherwise every extra
/// spelling the attacker registers would bring a budget of its own (ADR-0057).
/// </remarks>
internal interface IEmailConfirmationResendThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="mailbox"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(MailboxKey mailbox);
}
