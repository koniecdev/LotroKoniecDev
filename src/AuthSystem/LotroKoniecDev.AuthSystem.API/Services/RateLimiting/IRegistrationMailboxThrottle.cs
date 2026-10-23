namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// New accounts per inbox: the unique address is no limit when each <c>+tag</c> or Gmail dot registers
/// again and mails the same inbox (ADR-0057).
/// </summary>
internal interface IRegistrationMailboxThrottle
{
    bool TryAcquire(MailboxKey mailbox);
}
