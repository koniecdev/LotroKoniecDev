namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// The one clock behind the undo (ADR-0048), shared by the reservation (#684), the second cancel e-mail
/// (#685), the finalizer's erasure date and the number the revert e-mail prints.
/// </summary>
/// <remarks>
/// Measured from <c>EmailChangeRevertArmedAt</c>, stamped at the confirm. The token is minted a moment
/// later by the outbox processor, so the window closes slightly before the link does (ADR-0048 rule 4),
/// normally by seconds. A failing relay stretches the gap, but never to days, because delivery gets only
/// five retries before the dead-letter queue.
/// </remarks>
internal interface IEmailChangeRevertWindow
{
    /// <summary>
    /// The number the revert e-mail quotes, so it comes from the same place as the checks below.
    /// </summary>
    TimeSpan Lifespan { get; }

    /// <summary>
    /// <c>null</c> when nothing is armed, including a row with no timestamp.
    /// </summary>
    DateTimeOffset? ExpiresAt(DateTimeOffset? armedAt);

    bool IsLiveAt(DateTimeOffset? armedAt, DateTimeOffset moment);

    /// <summary>
    /// The cutoff for a database query: a row armed strictly after it is still live.
    /// </summary>
    DateTimeOffset LiveSince(DateTimeOffset moment);
}
