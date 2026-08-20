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
    /// Streams the whole catalog as untracked <see cref="StoredSourceDigest"/> rows for the import
    /// diff (spec 0006). Each source triple is hashed row by row and the strings are dropped, so the
    /// read holds one row at a time whatever the catalog size. This full scan is admin-only and runs
    /// about once per game update.
    /// </summary>
    IAsyncEnumerable<StoredSourceDigest> StreamSourceDigestsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads one chunk of tracked aggregates by id for the import (spec 0006). Callers keep the chunks
    /// small (see <c>ImportSettings.ApplyChunkSize</c>) and clear the change tracker between them.
    /// </summary>
    Task<IReadOnlyList<Translation>> GetByIdsAsync(IReadOnlyList<TranslationId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the tracked translation for a fragment identity, or <see cref="Maybe{T}.None"/> when the
    /// pair is unknown. The editor upsert path mutates the returned aggregate.
    /// </summary>
    Task<Maybe<Translation>> GetByFragmentKeyAsync(FragmentKey fragmentKey, CancellationToken cancellationToken);

    void InsertRange(IEnumerable<Translation> translations);

    /// <summary>
    /// Whether any translation points at the given game version, through introduction, source change
    /// or removal (spec 0001). Game-version deletion uses this: deleting a referenced version would
    /// leave those rows pointing at nothing.
    /// </summary>
    Task<bool> AnyReferencesGameVersionAsync(GameVersionId gameVersionId, CancellationToken cancellationToken);
}
