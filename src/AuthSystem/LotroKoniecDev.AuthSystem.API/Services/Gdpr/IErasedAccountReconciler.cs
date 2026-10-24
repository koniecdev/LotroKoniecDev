namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Two separate steps (ADR-0065), so one that keeps failing does not stop the other. Both are safe to
/// repeat.
/// </summary>
internal interface IErasedAccountReconciler
{
    /// <summary>
    /// Cuts every sent message of an erased account down to the account id, and returns how many it
    /// changed.
    /// </summary>
    Task<int> ScrubSentMessagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes an <c>AccountErased</c> message for each erased account that has none, and returns how many.
    /// It refuses a context that still tracks changes, which a failed erasure may have left behind.
    /// </summary>
    Task<int> AnnounceUnannouncedErasuresAsync(CancellationToken cancellationToken);
}
