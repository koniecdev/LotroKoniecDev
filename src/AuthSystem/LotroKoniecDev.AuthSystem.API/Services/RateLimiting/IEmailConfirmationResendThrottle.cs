namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Belongs to the inbox the mail reaches, not the caller (ADR-0055): the form is anonymous, and an
/// attacker who rotates IPs gets a fresh IP budget every time. Per inbox, not per account, or every
/// spelling a stranger registers would bring a fresh budget (ADR-0057).
/// </summary>
internal interface IEmailConfirmationResendThrottle
{
    bool TryAcquire(MailboxKey mailbox);
}
