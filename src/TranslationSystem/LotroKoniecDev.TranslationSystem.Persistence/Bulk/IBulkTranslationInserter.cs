using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;

namespace LotroKoniecDev.TranslationSystem.Persistence.Bulk;

/// <summary>
/// Writes new <see cref="Translation"/> rows straight into the table with PostgreSQL <c>COPY</c>
/// (Npgsql binary import). It skips the EF change tracker, which is what makes the import's
/// added-rows path fast (ADR-0011).
/// It uses the write <c>DbContext</c>'s own connection, so when the caller opens a transaction first
/// (see <see cref="Abstractions.IUnitOfWork.ExecuteInTransactionAsync"/>) the <c>COPY</c> joins it and
/// either the whole import lands or none of it does.
/// </summary>
public interface IBulkTranslationInserter
{
    /// <summary>
    /// Streams <paramref name="translations"/> into <c>translation."Translations"</c> as one binary
    /// <c>COPY</c>, row by row as the stream produces them. Nothing is collected into a list, so even
    /// a full-catalog baseline import uses constant memory (spec 0006, ADR-0011 amendment). An empty
    /// stream does nothing. Each row is written from the entity's own column values, so the entity
    /// stays the one place that defines the shape of a row (ADR-0011).
    /// </summary>
    Task InsertAsync(IAsyncEnumerable<Translation> translations, CancellationToken cancellationToken);
}
