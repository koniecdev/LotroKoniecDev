namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Brings the outbox in line with every erased account (ADR-0065). The two steps are separate, so one
/// that keeps failing does not stop the other. Both are safe to run any number of times: a second run
/// finds nothing left to do.
/// </summary>
internal interface IErasedAccountReconciler
{
    /// <summary>
    /// Cuts every sent message of an erased account down to the account id, and returns how many it
    /// changed.
    /// </summary>
    Task<int> ScrubSentMessagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes an <c>AccountErased</c> message for each erased account that has none yet, and returns
    /// how many it wrote. It saves through the shared <c>AuthDbContext</c>, so it refuses to run on a
    /// context that still tracks changes: a change a failed erasure left behind would be saved with
    /// its messages.
    /// </summary>
    Task<int> AnnounceUnannouncedErasuresAsync(CancellationToken cancellationToken);
}
