using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;

public interface ITranslationRepository : IRepository<Translation, TranslationId>
{
    /// <summary>
    /// Hashes each row as it streams and drops the strings, so memory stays flat whatever the catalog
    /// size (spec 0006). A full scan, but admin-only and about once per game update.
    /// </summary>
    IAsyncEnumerable<StoredSourceDigest> StreamSourceDigestsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns tracked aggregates. Callers keep chunks small (<c>ImportSettings.ApplyChunkSize</c>) and
    /// clear the change tracker between them (spec 0006).
    /// </summary>
    Task<IReadOnlyList<Translation>> GetByIdsAsync(IReadOnlyList<TranslationId> ids, CancellationToken cancellationToken);

    Task<Maybe<Translation>> GetByFragmentKeyAsync(FragmentKey fragmentKey, CancellationToken cancellationToken);

    void InsertRange(IEnumerable<Translation> translations);

    /// <summary>
    /// Counts introduction, source change and removal pointers (spec 0001).
    /// </summary>
    Task<bool> AnyReferencesGameVersionAsync(GameVersionId gameVersionId, CancellationToken cancellationToken);
}
