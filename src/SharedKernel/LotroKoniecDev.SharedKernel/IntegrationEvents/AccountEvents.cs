namespace LotroKoniecDev.SharedKernel.IntegrationEvents;

/// <summary>
/// The AuthSystem publishes to <see cref="Exchange"/>, and each context that cares declares its own
/// queue bound to the routing key it needs (ADR-0065).
/// </summary>
public static class AccountEvents
{
    /// <summary>
    /// Kept apart from the e-mail exchange, so an account-event consumer never receives e-mail work and
    /// the e-mail queue never receives account events.
    /// </summary>
    public const string Exchange = "lotro.accounts";

    public const string ErasedRoutingKey = "account.erased";
}
