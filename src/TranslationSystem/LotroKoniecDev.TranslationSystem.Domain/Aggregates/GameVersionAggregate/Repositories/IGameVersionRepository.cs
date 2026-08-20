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
    /// Loads the unprocessed versions detected before <paramref name="detectedAt"/>. These are the
    /// older versions an import supersedes when a newer one is processed (spec 0001). There are only
    /// a few of them, so the handler gets the aggregates and marks them one by one.
    /// </summary>
    Task<IReadOnlyList<GameVersion>> GetUnprocessedDetectedBeforeAsync(
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken);
}
