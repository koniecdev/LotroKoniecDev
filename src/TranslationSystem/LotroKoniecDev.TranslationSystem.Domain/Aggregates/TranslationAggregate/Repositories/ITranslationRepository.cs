using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;

public interface ITranslationRepository : IRepository<Translation, TranslationId>
{
    /// <summary>
    /// Loads every stored translation as tracked aggregates for the import diff, which compares
    /// the full uploaded export against the full stored source state by <c>(FileId, GossipId)</c>
    /// (spec 0001). Admin-only, infrequent — the whole-set load is acceptable within the
    /// proven ~780k-row envelope; revisit only if a real perf need appears.
    /// </summary>
    Task<IReadOnlyList<Translation>> GetAllAsync(CancellationToken cancellationToken);

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
