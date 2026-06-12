using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;

public interface IGameVersionRepository : IRepository<GameVersion, GameVersionId>
{
    Task<bool> ExistsByVersionAsync(LotroNotationVersion version, CancellationToken cancellationToken);
}
