using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;

public interface IUnitOfWork
{
    /// <summary>
    /// Saves all of the pending changes in the unit of work.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of entities that have been saved.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a transaction on the current unit of work.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new database context transaction.</returns>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> and then persists the unit's tracked changes inside a single
    /// database transaction owned by the provider's retrying execution strategy, committing them
    /// together (or rolling them all back on any failure). The context enables retry-on-failure, which
    /// forbids a manually started transaction outside a strategy-managed retry boundary — so a unit
    /// that spans more than one round-trip (e.g. a bulk <c>COPY</c> in <paramref name="operation"/>
    /// plus the tracked mutations saved here) must go through this method rather than a bare
    /// <see cref="SaveChangesAsync"/>. Tracked changes are accepted only after the commit succeeds, so
    /// a transient fault at commit re-runs the whole unit without losing them.
    /// </summary>
    /// <param name="operation">
    /// Extra work to enlist in the transaction before the tracked save (e.g. a bulk <c>COPY</c>);
    /// receives the caller's token.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the currently tracked changes as one chunk of a chunked apply inside an
    /// <see cref="ExecuteInTransactionAsync"/> unit, then clears the change tracker so the next
    /// chunk starts empty (spec 0006). The save defers accepting changes
    /// (<c>acceptAllChangesOnSuccess: false</c>), keeping every save inside the retrying unit on
    /// the same discipline; the clear also guarantees no tracked state survives into a retry.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SaveChangesAndClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches everything the unit of work tracks. A retrying transactional unit whose chunks
    /// clear the tracker calls this first, so a re-run after a transient commit fault never
    /// re-saves leftovers from the failed attempt.
    /// </summary>
    void ClearChangeTracker();
}
