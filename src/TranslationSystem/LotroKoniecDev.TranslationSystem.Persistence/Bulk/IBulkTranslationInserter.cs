using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;

namespace LotroKoniecDev.TranslationSystem.Persistence.Bulk;

/// <summary>
/// Inserts new <see cref="Translation"/> rows with a binary <c>COPY</c>, skipping the change tracker,
/// which makes the import's added-rows path fast (ADR-0011). Inside
/// <see cref="Abstractions.IUnitOfWork.ExecuteInTransactionAsync"/> the <c>COPY</c> joins the
/// transaction, so the whole import lands or none of it does.
/// </summary>
public interface IBulkTranslationInserter
{
    /// <summary>
    /// Streams row by row, so even a full-catalog baseline import uses constant memory (spec 0006,
    /// ADR-0011 amendment). An empty stream does nothing.
    /// </summary>
    Task InsertAsync(IAsyncEnumerable<Translation> translations, CancellationToken cancellationToken);
}
