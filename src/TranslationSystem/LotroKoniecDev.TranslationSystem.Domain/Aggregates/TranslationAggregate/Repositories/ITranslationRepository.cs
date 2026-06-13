using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
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

    void InsertRange(IEnumerable<Translation> translations);
}
