namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A budget of new accounts per inbox. Each registration mails the typed address, and the unique address
/// is no budget when an inbox has many spellings: each <c>+tag</c> or Gmail dot registers again and mails
/// the same inbox again (ADR-0057).
/// </summary>
internal interface IRegistrationMailboxThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="mailbox"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(MailboxKey mailbox);
}
