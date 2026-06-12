using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;

public interface IGameVersionRepository : IRepository<GameVersion, GameVersionId>
{
    Task<bool> ExistsByVersionAsync(string version, CancellationToken cancellationToken);
}
