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
}
