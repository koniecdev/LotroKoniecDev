using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;

namespace LotroKoniecDev.TranslationSystem.Persistence.Bulk;

/// <summary>
/// Bulk-writes added <see cref="Translation"/> rows straight to the table with PostgreSQL <c>COPY</c>
/// (Npgsql binary import), bypassing the EF change tracker for the import's added-rows hot path
/// (ADR-0011). It runs on the write <c>DbContext</c>'s own connection, so a caller that opens a
/// transaction first (see <see cref="Abstractions.IUnitOfWork.ExecuteInTransactionAsync"/>) gets the
/// <c>COPY</c> enlisted in that transaction — all-or-nothing with the rest of the import.
/// </summary>
public interface IBulkTranslationInserter
{
    /// <summary>
    /// Streams <paramref name="translations"/> into <c>translation."Translations"</c> as a single
    /// binary <c>COPY</c>. A no-op for an empty collection. Each row is serialized from the entity's
    /// own column values, so the entity stays the single definition of a row's shape (ADR-0011).
    /// </summary>
    Task InsertAsync(IReadOnlyCollection<Translation> translations, CancellationToken cancellationToken);
}
