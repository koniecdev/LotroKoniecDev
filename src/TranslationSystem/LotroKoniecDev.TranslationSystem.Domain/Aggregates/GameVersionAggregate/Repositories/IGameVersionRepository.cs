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
    /// Loads the tracked, still-<see cref="GameVersionStatus.Unprocessed"/> versions detected before
    /// <paramref name="detectedAt"/> — the stacked older versions an import supersedes when a newer one
    /// is processed (spec 0001). A handful of rows at most, so it returns the aggregates for the handler
    /// to mass-mark; the version being processed is excluded by the strict "detected before" bound.
    /// </summary>
    Task<IReadOnlyList<GameVersion>> GetUnprocessedDetectedBeforeAsync(
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken);
}
