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
    /// Streams the whole catalog as untracked <see cref="StoredSourceDigest"/> value rows for the
    /// import diff (spec 0006): the source triple is hashed row-by-row and the strings discarded,
    /// so the read's working set is one row regardless of catalog size. The full-catalog scan is
    /// admin-only and roughly once per game update.
    /// </summary>
    IAsyncEnumerable<StoredSourceDigest> StreamSourceDigestsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads one chunk of tracked aggregates by id for the import's chunked apply (spec 0006) —
    /// callers keep chunks small (see <c>ImportSettings.ApplyChunkSize</c>) and clear the change
    /// tracker between chunks.
    /// </summary>
    Task<IReadOnlyList<Translation>> GetByIdsAsync(IReadOnlyList<TranslationId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the single tracked translation for a fragment identity, or <see cref="Maybe{T}.None"/>
    /// when the pair is unknown. Backs the editor upsert path, which mutates the returned aggregate.
    /// </summary>
    Task<Maybe<Translation>> GetByFragmentKeyAsync(FragmentKey fragmentKey, CancellationToken cancellationToken);

    void InsertRange(IEnumerable<Translation> translations);

    /// <summary>
    /// Whether any stored translation is bound to the given game version — by introduction, source
    /// change or removal (spec 0001). Guards game-version deletion: a referenced version must never be
    /// removed, or those rows would point at a missing version.
    /// </summary>
    Task<bool> AnyReferencesGameVersionAsync(GameVersionId gameVersionId, CancellationToken cancellationToken);
}
