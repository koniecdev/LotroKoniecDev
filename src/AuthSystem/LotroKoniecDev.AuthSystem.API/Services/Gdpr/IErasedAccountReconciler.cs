namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

internal interface IErasedAccountReconciler
{
    /// <summary>
    /// Brings the outbox in line with every erased account (ADR-0065): it cuts their sent messages down
    /// to the account id, and it writes an <c>AccountErased</c> message for each one that has none yet.
    /// Safe to run any number of times. A second run finds nothing left to do.
    /// </summary>
    /// <remarks>
    /// It saves through the shared <c>AuthDbContext</c>, so the caller hands it a context with nothing
    /// tracked: a change a failed erasure left behind would be saved with its messages.
    /// </remarks>
    Task<ErasedAccountReconciliation> ReconcileAsync(CancellationToken cancellationToken);
}
