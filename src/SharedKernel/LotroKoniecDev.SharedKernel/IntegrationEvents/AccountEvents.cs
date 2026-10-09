namespace LotroKoniecDev.SharedKernel.IntegrationEvents;

/// <summary>
/// Where account events travel on the broker. The AuthSystem publishes to <see cref="Exchange"/>, and
/// each context that cares declares its own queue bound to the routing key it needs (ADR-0065).
/// </summary>
public static class AccountEvents
{
    /// <summary>
    /// A topic exchange of its own, apart from the e-mail exchange. A consumer of account events must
    /// never receive e-mail work, and the e-mail queue must never receive account events.
    /// </summary>
    public const string Exchange = "lotro.accounts";

    public const string ErasedRoutingKey = "account.erased";
}
