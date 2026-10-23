namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Belongs to the new address's inbox, whichever account asks (ADR-0055, ADR-0057).
/// </summary>
internal interface IEmailChangeRecipientThrottle
{
    bool TryAcquire(MailboxKey mailbox);
}
