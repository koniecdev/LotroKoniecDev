using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;

public interface IGameVersionRepository : IRepository<GameVersion, GameVersionId>
{
    Task<bool> ExistsByVersionAsync(LotroNotationVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// The older versions an import supersedes (spec 0001). There are only a few, so the handler loads
    /// the aggregates and marks them one by one.
    /// </summary>
    Task<IReadOnlyList<GameVersion>> GetUnprocessedDetectedBeforeAsync(
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken);
}
