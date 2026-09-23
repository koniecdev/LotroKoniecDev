namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A send budget for e-mail change links that belongs to the new address, whichever account asks
/// (ADR-0055). The new address has no account by definition, so there is no id to key on.
/// </summary>
internal interface IEmailChangeRecipientThrottle
{
    /// <summary>
    /// Takes one permit for <paramref name="normalizedEmail"/> and reports whether there was one left.
    /// Pass the address through <c>UserManager.NormalizeEmail</c> first, so every spelling Identity
    /// would treat as one account shares one budget.
    /// </summary>
    bool TryAcquire(string normalizedEmail);
}
