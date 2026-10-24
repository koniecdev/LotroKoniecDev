using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;

public interface IUnitOfWork
{
    /// <returns>The number of entities that were saved.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> and then saves the tracked changes in one transaction owned by
    /// the retrying execution strategy. Retry-on-failure forbids a hand-started transaction, so work
    /// with more than one round trip (a bulk <c>COPY</c> plus tracked changes) must come through here.
    /// A temporary fault at commit re-runs the whole unit, <paramref name="operation"/> included.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// One chunk of a chunked apply inside <see cref="ExecuteInTransactionAsync"/> (spec 0006). The
    /// tracker is cleared so the next chunk starts empty and nothing survives into a retry.
    /// </summary>
    Task SaveChangesAndClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A retrying transaction whose chunks clear the tracker calls this first, so a re-run after a
    /// failed commit never saves leftovers from the attempt before.
    /// </summary>
    void ClearChangeTracker();
}
