namespace LotroKoniecDev.AuthSystem.API.Services.Accounts;

/// <summary>
/// The one clock behind the undo of ADR-0048. Three things depend on how long an armed undo lives:
/// the address reservation of #684, the second cancel e-mail of #685, and the date the finalizer
/// erases an account. They used to work it out separately, and a clock copied three times is a clock
/// that drifts.
/// </summary>
/// <remarks>
/// The window is measured from <c>ApplicationUser.EmailChangeRevertArmedAt</c>, which is stamped when
/// the change is confirmed. The token itself is minted a moment later, by the outbox processor, so
/// this window closes marginally before the link does. ADR-0048 rule 4 records that gap and why it
/// stays: the dispatch delay is seconds, and delivery is capped at five attempts before the message
/// is dead-lettered, so it cannot grow to days.
/// </remarks>
internal interface IEmailChangeRevertWindow
{
    /// <summary>
    /// When the undo armed at <paramref name="armedAt"/> stops working, or <c>null</c> when nothing is
    /// armed. A row armed before #684 has no timestamp and reads as nothing armed, which is the
    /// behavior it shipped under.
    /// </summary>
    DateTimeOffset? ExpiresAt(DateTimeOffset? armedAt);

    /// <summary>
    /// Whether that undo can still be used at <paramref name="moment"/>.
    /// </summary>
    bool IsLiveAt(DateTimeOffset? armedAt, DateTimeOffset moment);

    /// <summary>
    /// The cutoff a database query compares <c>EmailChangeRevertArmedAt</c> against: a row armed
    /// strictly after it is still live. It exists because a query needs a value to compare a column
    /// with, not a method it cannot translate.
    /// </summary>
    DateTimeOffset LiveSince(DateTimeOffset moment);
}
