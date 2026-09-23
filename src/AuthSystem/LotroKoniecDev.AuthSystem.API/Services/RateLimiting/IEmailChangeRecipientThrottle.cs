namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for e-mail change links that belongs to the inbox of the new address, whichever account
/// asks (ADR-0055, ADR-0057).
/// </summary>
internal interface IEmailChangeRecipientThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="mailbox"/> and reports whether there was one left.
    /// </summary>
    bool TryAcquire(MailboxKey mailbox);
}
