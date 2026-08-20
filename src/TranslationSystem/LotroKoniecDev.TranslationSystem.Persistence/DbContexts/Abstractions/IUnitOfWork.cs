using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;

public interface IUnitOfWork
{
    /// <returns>The number of entities that were saved.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> and then saves the tracked changes, both in one database
    /// transaction that the provider's retrying execution strategy owns. They commit together, or
    /// they all roll back.
    /// The context has retry-on-failure turned on, and that forbids starting a transaction by hand
    /// outside the retry boundary. So work that needs more than one round trip, such as a bulk
    /// <c>COPY</c> in <paramref name="operation"/> plus the tracked changes saved here, has to go
    /// through this method instead of a plain <see cref="SaveChangesAsync"/>.
    /// The tracked changes are accepted only after the commit succeeds, so a temporary fault at
    /// commit time re-runs the whole unit without losing them.
    /// </summary>
    /// <param name="operation">
    /// Extra work to run inside the transaction before the tracked save, such as a bulk <c>COPY</c>.
    /// It receives the caller's token.
    /// </param>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the tracked changes as one chunk of a chunked apply inside an
    /// <see cref="ExecuteInTransactionAsync"/> unit, then clears the change tracker so the next chunk
    /// starts empty (spec 0006). The save does not accept the changes yet
    /// (<c>acceptAllChangesOnSuccess: false</c>), so every save inside the retrying unit follows the
    /// same rule, and clearing the tracker makes sure nothing survives into a retry.
    /// </summary>
    Task SaveChangesAndClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches everything the unit of work tracks. A retrying transaction whose chunks clear the
    /// tracker calls this first, so a re-run after a failed commit never saves leftovers from the
    /// attempt before.
    /// </summary>
    void ClearChangeTracker();
}
